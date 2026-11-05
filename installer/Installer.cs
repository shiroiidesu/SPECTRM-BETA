using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace Spectrm.Setup
{
    internal sealed class InstallChoices
    {
        public string Dir;
        public bool Desktop;
        public bool StartMenu;
    }

    /// <summary>Install, update and uninstall steps. Per-user: nothing here needs admin rights.</summary>
    internal static class Installer
    {
        public const string AppName = "SPECTRM";
        public const string ExeName = "SPECTRM.exe";
        public const string UninstallerName = "Uninstall SPECTRM.exe";
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SPECTRM";

        public static string SelfPath => Assembly.GetEntryAssembly().Location;

        private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        public static string DefaultDir => Path.Combine(LocalAppData, "Programs", AppName);
        public static string SettingsDir => Path.Combine(LocalAppData, AppName);
        public static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");
        public static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

        /// <summary>Folder of an existing install, from the "Apps &amp; features" entry; null if not installed.</summary>
        public static string ExistingDir()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(UninstallKey))
            {
                var dir = key?.GetValue("InstallLocation") as string;
                return !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, ExeName)) ? dir : null;
            }
        }

        public static string InstalledVersion()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(UninstallKey))
                return key?.GetValue("DisplayVersion") as string;
        }

        public static bool IsInside(string file, string dir)
        {
            if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(dir)) return false;
            var folder = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            return Path.GetFullPath(file).StartsWith(folder, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Installs into a "SPECTRM" folder inside whatever the user picked (unless they picked one already),
        /// so uninstalling never touches their other files.
        /// </summary>
        public static string NormaliseDir(string dir)
        {
            dir = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dir.Trim().Trim('"')));
            var trimmed = dir.TrimEnd('\\');
            return string.Equals(Path.GetFileName(trimmed), AppName, StringComparison.OrdinalIgnoreCase)
                ? trimmed
                : Path.Combine(dir, AppName);
        }

        public static void Install(Payload payload, InstallChoices choices, Action<float, string> report)
        {
            report = report ?? ((p, s) => { });
            var dir = choices.Dir;
            report(0.02f, "Preparing");
            Directory.CreateDirectory(dir);
            CloseRunning(dir, report);

            var exe = Path.Combine(dir, ExeName);
            var unpacked = exe + ".new";
            payload.ExtractTo(unpacked, p => report(0.05f + 0.85f * p, "Unpacking SPECTRM"));
            report(0.9f, "Finishing up");
            Retry(() =>
            {
                if (File.Exists(exe)) File.Delete(exe);
                File.Move(unpacked, exe);
            });
            payload.WriteStub(Path.Combine(dir, UninstallerName));

            report(0.94f, "Creating shortcuts");
            UpdateShortcut(StartMenuLink, exe, choices.StartMenu);
            UpdateShortcut(DesktopLink, exe, choices.Desktop);

            report(0.97f, "Registering with Windows");
            Register(dir, payload);
            report(1f, "Done");
        }

        public static void Uninstall(string dir, bool removeSettings, Action<float, string> report)
        {
            report = report ?? ((p, s) => { });
            report(0.1f, "Closing SPECTRM");
            CloseRunning(dir, report);

            report(0.35f, "Removing files");
            var exe = Path.Combine(dir, ExeName);
            Retry(() => DeleteIfExists(exe));
            DeleteIfExists(exe + ".new");
            var uninstaller = Path.Combine(dir, UninstallerName);
            if (!string.Equals(Path.GetFullPath(uninstaller), Path.GetFullPath(SelfPath), StringComparison.OrdinalIgnoreCase))
                Retry(() => DeleteIfExists(uninstaller));

            report(0.65f, "Removing shortcuts");
            foreach (var link in new[] { StartMenuLink, DesktopLink })
                if (File.Exists(link) && IsInside(ShortcutTarget(link), dir)) DeleteIfExists(link);

            report(0.8f, "Unregistering");
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            if (removeSettings && Directory.Exists(SettingsDir)) Directory.Delete(SettingsDir, true);
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch (IOException) { }
            report(1f, "Done");
        }

        /// <summary>Closes SPECTRM if it is running from <paramref name="dir"/> (asks it to close first, then forces).</summary>
        private static void CloseRunning(string dir, Action<float, string> report)
        {
            foreach (var process in Process.GetProcessesByName(AppName))
            {
                using (process)
                {
                    string path;
                    try { path = process.MainModule.FileName; }
                    catch (Exception) { continue; }
                    if (!IsInside(path, dir)) continue;
                    report(0.03f, "Closing SPECTRM");
                    try
                    {
                        process.CloseMainWindow();
                        if (!process.WaitForExit(4000))
                        {
                            process.Kill();
                            process.WaitForExit(3000);
                        }
                    }
                    catch (Exception) { }
                }
            }
        }

        private static void Register(string dir, Payload payload)
        {
            var exe = Path.Combine(dir, ExeName);
            var uninstaller = Path.Combine(dir, UninstallerName);
            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", AppName);
                key.SetValue("DisplayVersion", payload.Version);
                key.SetValue("Publisher", AppName);
                key.SetValue("DisplayIcon", exe + ",0");
                key.SetValue("InstallLocation", dir);
                key.SetValue("UninstallString", "\"" + uninstaller + "\" /uninstall");
                key.SetValue("QuietUninstallString", "\"" + uninstaller + "\" /uninstall /quiet");
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                key.SetValue("EstimatedSize", (int)((payload.OriginalLength + 1023) / 1024), RegistryValueKind.DWord);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        /// <summary>Creates the shortcut, or removes an earlier SPECTRM shortcut when the box was unticked.</summary>
        private static void UpdateShortcut(string linkPath, string target, bool wanted)
        {
            if (wanted)
            {
                CreateShortcut(linkPath, target);
                return;
            }
            if (File.Exists(linkPath) && string.Equals(ShortcutTarget(linkPath), target, StringComparison.OrdinalIgnoreCase))
                DeleteIfExists(linkPath);
        }

        private static void CreateShortcut(string linkPath, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath));
            WithShortcut(linkPath, (type, link) =>
            {
                type.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { target });
                type.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { Path.GetDirectoryName(target) });
                type.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { target + ",0" });
                type.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { "SPECTRM real-time audio meter" });
                type.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
                return null;
            });
        }

        private static string ShortcutTarget(string linkPath)
        {
            try
            {
                return WithShortcut(linkPath, (type, link) => type.InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null) as string);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Opens a .lnk through Windows Script Host's shell object (always present on Windows).</summary>
        private static string WithShortcut(string linkPath, Func<Type, object, string> action)
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            var shell = Activator.CreateInstance(shellType);
            try
            {
                var link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { linkPath });
                try { return action(link.GetType(), link); }
                finally { Marshal.FinalReleaseComObject(link); }
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }

        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        /// <summary>Retries briefly: a just-closed SPECTRM or antivirus can hold a file for a moment.</summary>
        private static void Retry(Action action)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    action();
                    return;
                }
                catch (Exception e) when ((e is IOException || e is UnauthorizedAccessException) && attempt < 12)
                {
                    Thread.Sleep(250);
                }
            }
        }

        /// <summary>
        /// The uninstaller lives in the folder it deletes, so it first copies itself to %TEMP% and runs from there.
        /// </summary>
        public static void RelaunchFromTemp(string dir, bool quiet)
        {
            var temp = Path.Combine(Path.GetTempPath(), "SPECTRM-Uninstall-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
            File.Copy(SelfPath, temp, true);
            var arguments = "/uninstall /fromtemp \"/dir=" + dir.TrimEnd('\\') + "\"" + (quiet ? " /quiet" : "");
            Process.Start(new ProcessStartInfo(temp, arguments) { UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        }

        /// <summary>Deletes this temporary uninstaller copy a moment after it exits.</summary>
        public static void ScheduleSelfDelete()
        {
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 > nul & del /f /q \"" + SelfPath + "\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
            }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// The app bundled at the end of the setup exe: [setup program][gzip(SPECTRM.exe)][version][footer].
    /// Footer: int32 version length, int64 compressed length, int64 original length, 8-byte "SPECTRM1".
    /// </summary>
    internal sealed class Payload
    {
        private const string Magic = "SPECTRM1";
        private const int FooterSize = 4 + 8 + 8 + 8;

        private string _path;
        public long StubLength;
        public long CompressedLength;
        public long OriginalLength;
        public string Version;

        public static Payload TryRead(string path)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                using (var reader = new BinaryReader(stream))
                {
                    if (stream.Length < FooterSize) return null;
                    stream.Seek(-FooterSize, SeekOrigin.End);
                    var versionLength = reader.ReadInt32();
                    var compressed = reader.ReadInt64();
                    var original = reader.ReadInt64();
                    if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != Magic) return null;
                    if (versionLength < 0 || versionLength > 256 || compressed <= 0) return null;
                    stream.Seek(-FooterSize - versionLength, SeekOrigin.End);
                    var version = Encoding.UTF8.GetString(reader.ReadBytes(versionLength));
                    var stub = stream.Length - FooterSize - versionLength - compressed;
                    if (stub <= 0) return null;
                    return new Payload { _path = path, StubLength = stub, CompressedLength = compressed, OriginalLength = original, Version = version };
                }
            }
            catch (IOException)
            {
                return null;
            }
        }

        public void ExtractTo(string target, Action<float> progress)
        {
            using (var source = File.OpenRead(_path))
            {
                source.Seek(StubLength, SeekOrigin.Begin);
                var slice = new SliceStream(source, CompressedLength);
                using (var gzip = new GZipStream(slice, CompressionMode.Decompress))
                using (var output = File.Create(target))
                {
                    var buffer = new byte[1 << 20];
                    int read;
                    var lastReport = -1;
                    while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, read);
                        var percent = (int)(slice.Position * 100 / CompressedLength);
                        if (percent != lastReport)
                        {
                            lastReport = percent;
                            progress(percent / 100f);
                        }
                    }
                }
            }
            if (new FileInfo(target).Length != OriginalLength)
                throw new IOException("The installer looks damaged (unpacked size mismatch). Please download it again.");
        }

        /// <summary>Writes just the setup program (no bundled app): that is the uninstaller.</summary>
        public void WriteStub(string target)
        {
            using (var source = File.OpenRead(_path))
            using (var output = File.Create(target))
            {
                var buffer = new byte[81920];
                var remaining = StubLength;
                while (remaining > 0)
                {
                    var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read <= 0) throw new EndOfStreamException();
                    output.Write(buffer, 0, read);
                    remaining -= read;
                }
            }
        }

        /// <summary>Read-only view of the next <c>length</c> bytes of a stream.</summary>
        private sealed class SliceStream : Stream
        {
            private readonly Stream _inner;
            private readonly long _length;
            private long _position;

            public SliceStream(Stream inner, long length)
            {
                _inner = inner;
                _length = length;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var remaining = _length - _position;
                if (remaining <= 0) return 0;
                var read = _inner.Read(buffer, offset, (int)Math.Min(count, remaining));
                _position += read;
                return read;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _length;
            public override long Position { get => _position; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
