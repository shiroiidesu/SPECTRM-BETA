using System;
using System.IO;
using System.Windows.Forms;

namespace Spectrm.Setup
{
    /// <summary>
    /// Command line:
    ///   (none)                  install, with the setup window
    ///   /quiet                  install silently with defaults (desktop + Start menu shortcuts)
    ///   /dir="C:\path"          install / uninstall folder
    ///   /uninstall [/quiet]     uninstall (this is what "Apps &amp; features" runs)
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var options = CommandLine.Parse(args);
            var payload = Payload.TryRead(Installer.SelfPath);

            // Without a bundled app this exe is the uninstaller copy in the install folder.
            if (options.Uninstall || payload == null)
            {
                var dir = options.Dir ?? Path.GetDirectoryName(Installer.SelfPath);
                if (!options.FromTemp && Installer.IsInside(Installer.SelfPath, dir))
                {
                    Installer.RelaunchFromTemp(dir, options.Quiet);
                    return 0;
                }
                try
                {
                    if (options.Quiet)
                    {
                        Installer.Uninstall(dir, options.RemoveSettings, null);
                        return 0;
                    }
                    Application.Run(new SetupWindow(null, dir));
                    return 0;
                }
                catch (Exception)
                {
                    return 1;
                }
                finally
                {
                    if (options.FromTemp) Installer.ScheduleSelfDelete();
                }
            }

            if (options.Quiet)
            {
                try
                {
                    var dir = options.Dir != null ? Installer.NormaliseDir(options.Dir) : Installer.ExistingDir() ?? Installer.DefaultDir;
                    Installer.Install(payload, new InstallChoices { Dir = dir, Desktop = true, StartMenu = true }, null);
                    return 0;
                }
                catch (Exception)
                {
                    return 1;
                }
            }

            Application.Run(new SetupWindow(payload, null));
            return 0;
        }
    }

    internal sealed class CommandLine
    {
        public bool Uninstall;
        public bool Quiet;
        public bool FromTemp;
        public bool RemoveSettings;
        public string Dir;

        public static CommandLine Parse(string[] args)
        {
            var result = new CommandLine();
            foreach (var raw in args)
            {
                var arg = raw.Trim();
                var lower = arg.ToLowerInvariant();
                if (lower == "/uninstall" || lower == "--uninstall") result.Uninstall = true;
                else if (lower == "/quiet" || lower == "/s" || lower == "--quiet") result.Quiet = true;
                else if (lower == "/fromtemp") result.FromTemp = true;
                else if (lower == "/removesettings") result.RemoveSettings = true;
                else if (lower.StartsWith("/dir=")) result.Dir = arg.Substring(5).Trim('"');
            }
            return result;
        }
    }
}
