// SPECTRM - real-time audio meter for Windows
// Copyright (C) 2026 shiroiidesu
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This program is free software: you can redistribute it and/or modify it under the terms of the GNU General
// Public License as published by the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version. It is distributed WITHOUT ANY WARRANTY; see the LICENSE file for details.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Spectrm.Setup
{
    /// <summary>The setup window: install (or update), progress, done; or uninstall, progress, removed.</summary>
    internal sealed class SetupWindow : Form
    {
        private enum Page { Install, Progress, Done, Uninstall, Removed }

        // Layout in 96-DPI pixels; D() scales to the screen.
        private const int WindowWidth = 560, WindowHeight = 372, Gutter = 36, ContentWidth = WindowWidth - 2 * Gutter;
        private const int FooterTop = 300;

        private readonly Payload _payload;      // null when uninstalling
        private readonly string _uninstallDir;  // null when installing
        private readonly string _existingDir;
        private readonly string _existingVersion;
        private readonly float _scale;

        private readonly Font _headingFont;
        private readonly Font _bodyFont;
        private readonly Font _labelFont;
        private readonly Font _errorFont;
        private readonly Font _buttonFont;
        private readonly GraphicsPath _logo;

        private readonly Panel _pathPanel = new();
        private readonly TextBox _pathText = new();
        private readonly FlatButton _browse = new();
        private readonly ToggleRow _desktop = new();
        private readonly ToggleRow _startMenu = new();
        private readonly ToggleRow _launch = new();
        private readonly ToggleRow _removeSettings = new();
        private readonly FlatButton _primary = new();
        private readonly FlatButton _secondary = new();
        private readonly FlatButton _close = new();
        private readonly Timer _animation = new() { Interval = 15 };

        private Page _page;
        private string _heading = "";
        private string _detail = "";
        private string _error = "";
        private string _step = "";
        private float _progress;
        private float _shownProgress;
        private float _shimmer;
        private bool _busy;
        private string _installedDir;

        public SetupWindow(Payload payload, string uninstallDir)
        {
            _payload = payload;
            _uninstallDir = uninstallDir;
            if (payload != null)
            {
                _existingDir = Installer.ExistingDir();
                _existingVersion = _existingDir != null ? Installer.InstalledVersion() : null;
            }

            using (var g = CreateGraphics()) _scale = g.DpiX / 96f;
            _headingFont = new Font(Theme.Display, 15f, FontStyle.Bold);
            _bodyFont = new Font(Theme.Text, 9.5f, FontStyle.Bold);
            _labelFont = new Font(Theme.Small, 7.5f, FontStyle.Bold);
            _errorFont = new Font(Theme.Small, 8f, FontStyle.Bold);
            _buttonFont = new Font(Theme.Text, 9f, FontStyle.Bold);

            _logo = LogoArt.CreateFull();
            var bounds = _logo.GetBounds();
            var logoHeight = D(22);
            LogoArt.FitInto(_logo, new RectangleF(D(Gutter), D(36), bounds.Width * logoHeight / bounds.Height, logoHeight));

            Text = payload != null ? "SPECTRM Setup" : "Uninstall SPECTRM";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(D(WindowWidth), D(WindowHeight));
            BackColor = Theme.Background;
            ForeColor = Theme.Ink;
            Font = _bodyFont;
            DoubleBuffered = true;
            KeyPreview = true;
            using (var stream = typeof(SetupWindow).Assembly.GetManifestResourceStream("SPECTRM.ico"))
                if (stream != null) Icon = new Icon(stream);

            BuildControls();
            _animation.Tick += (_, _) => Animate();
            SetPage(payload != null ? Page.Install : Page.Uninstall);
        }

        private int D(int px) => (int)Math.Round(px * _scale);

        private void BuildControls()
        {
            _close.Style = FlatButton.Kind.TitleBar;
            _close.Text = "";
            _close.Font = new Font("Segoe MDL2 Assets", 7.5f);
            _close.AccessibleName = "Close";
            _close.TabStop = false;
            _close.Bounds = new Rectangle(D(WindowWidth - 46), Math.Max(2, D(3)), D(46), D(30)); // below the brand strip
            _close.Click += (_, _) => Close();

            // Install folder: a dark box with the path and a Browse button.
            _pathPanel.BackColor = Theme.Track;
            _pathPanel.Bounds = new Rectangle(D(Gutter), D(162), D(ContentWidth), D(40));
            _pathPanel.Paint += (_, e) =>
            {
                using (var pen = new Pen(_pathText.Focused ? Theme.Mix(Theme.Line, Theme.Accent, 0.6f) : Theme.Line))
                    e.Graphics.DrawRectangle(pen, 0, 0, _pathPanel.Width - 1, _pathPanel.Height - 1);
            };
            _pathText.BorderStyle = BorderStyle.None;
            _pathText.BackColor = Theme.Track;
            _pathText.ForeColor = Theme.Ink;
            _pathText.Font = new Font(Theme.Text, 9.5f, FontStyle.Bold);
            _pathText.AccessibleName = "Install folder";
            _pathText.Bounds = new Rectangle(D(12), (D(40) - _pathText.PreferredHeight) / 2, D(ContentWidth - 12 - 96), _pathText.PreferredHeight);
            _pathText.GotFocus += (_, _) => _pathPanel.Invalidate();
            _pathText.LostFocus += (_, _) => _pathPanel.Invalidate();
            _pathText.TextChanged += (_, _) => ClearError();
            _browse.Text = "BROWSE";
            _browse.Font = _buttonFont;
            _browse.Bounds = new Rectangle(D(ContentWidth - 88), D(6), D(82), D(28));
            _browse.Click += (_, _) => Browse();
            _pathPanel.Controls.Add(_pathText);
            _pathPanel.Controls.Add(_browse);

            foreach (var toggle in new[] { _desktop, _startMenu, _launch, _removeSettings })
            {
                toggle.DpiScale = _scale;
                toggle.Font = _bodyFont;
                toggle.Size = new Size(D(ContentWidth), D(26));
            }
            _desktop.Text = "Create a desktop shortcut";
            _desktop.Checked = true;
            _desktop.Location = new Point(D(Gutter), D(214));
            _startMenu.Text = "Add to the Start menu";
            _startMenu.Checked = true;
            _startMenu.Location = new Point(D(Gutter), D(244));
            _launch.Text = "Open SPECTRM now";
            _launch.Checked = true;
            _launch.Location = new Point(D(Gutter + 38), D(184));
            _removeSettings.Text = "Also delete my SPECTRM settings";
            _removeSettings.Location = new Point(D(Gutter), D(184));

            _primary.Font = _buttonFont;
            _primary.Bounds = new Rectangle(D(WindowWidth - Gutter - 132), D(FooterTop + 18), D(132), D(38));
            _primary.Click += (_, _) => PrimaryClicked();
            _secondary.Font = _buttonFont;
            _secondary.Text = "CANCEL";
            _secondary.Bounds = new Rectangle(_primary.Left - D(10 + 104), _primary.Top, D(104), _primary.Height);
            _secondary.Click += (_, _) => Close();

            Controls.AddRange(new Control[] { _close, _pathPanel, _desktop, _startMenu, _launch, _removeSettings, _primary, _secondary });
        }

        private void SetPage(Page page)
        {
            _page = page;
            _pathPanel.Visible = page == Page.Install;
            _desktop.Visible = page == Page.Install;
            _startMenu.Visible = page == Page.Install;
            _launch.Visible = page == Page.Done;
            _removeSettings.Visible = page == Page.Uninstall;
            _primary.Visible = page != Page.Progress;
            _secondary.Visible = page == Page.Install || page == Page.Uninstall;
            _close.Enabled = page != Page.Progress;
            _primary.Style = page == Page.Uninstall ? FlatButton.Kind.Danger : FlatButton.Kind.Primary;

            switch (page)
            {
                case Page.Install:
                    _heading = _existingDir != null ? "Update SPECTRM" : "Install SPECTRM";
                    _detail = "";
                    if (string.IsNullOrEmpty(_pathText.Text)) _pathText.Text = _existingDir ?? Installer.DefaultDir;
                    _primary.Text = _existingDir == null ? "INSTALL"
                        : string.Equals(_existingVersion, _payload.Version, StringComparison.OrdinalIgnoreCase) ? "REINSTALL" : "UPDATE";
                    break;
                case Page.Progress:
                    _progress = _shownProgress = 0f;
                    _step = "Preparing";
                    break;
                case Page.Done:
                    _heading = "SPECTRM is ready";
                    _detail = _startMenu.Checked && _desktop.Checked ? "You'll find it in the Start menu and on your desktop."
                        : _startMenu.Checked ? "You'll find it in the Start menu."
                        : _desktop.Checked ? "You'll find it on your desktop."
                        : "Installed to " + _installedDir;
                    _primary.Text = "FINISH";
                    break;
                case Page.Uninstall:
                    _heading = "Uninstall SPECTRM?";
                    _detail = "This removes SPECTRM and its shortcuts from this PC.";
                    _primary.Text = "UNINSTALL";
                    break;
                case Page.Removed:
                    _heading = "SPECTRM was removed";
                    _detail = _removeSettings.Checked ? "The app and your settings are gone." : "Your settings were kept, in case you reinstall.";
                    _primary.Text = "CLOSE";
                    break;
            }
            if (page == Page.Progress) _animation.Start(); else _animation.Stop();
            Invalidate();
            if (_primary.Visible) _primary.Focus();
        }

        private void PrimaryClicked()
        {
            switch (_page)
            {
                case Page.Install: StartInstall(); break;
                case Page.Done: Finish(); break;
                case Page.Uninstall: StartUninstall(); break;
                default: Close(); break;
            }
        }

        private async void StartInstall()
        {
            string dir;
            try
            {
                dir = Installer.NormaliseDir(_pathText.Text);
            }
            catch (Exception)
            {
                ShowError("That folder path isn't valid.");
                return;
            }
            _pathText.Text = dir;
            var choices = new InstallChoices { Dir = dir, Desktop = _desktop.Checked, StartMenu = _startMenu.Checked };
            _heading = _existingDir != null ? "Updating SPECTRM" : "Installing SPECTRM";
            _busy = true;
            SetPage(Page.Progress);
            try
            {
                await Task.Run(() => Installer.Install(_payload, choices, Report));
                _installedDir = dir;
                await Task.Delay(350); // let the bar reach the end
                _busy = false;
                SetPage(Page.Done);
            }
            catch (UnauthorizedAccessException)
            {
                Failed("Windows didn't allow SPECTRM to be installed there. Choose a folder in your user profile, like the default one.");
            }
            catch (Exception e)
            {
                Failed("Install failed: " + e.Message);
            }
        }

        private async void StartUninstall()
        {
            _heading = "Removing SPECTRM";
            _busy = true;
            SetPage(Page.Progress);
            try
            {
                var removeSettings = _removeSettings.Checked;
                await Task.Run(() => Installer.Uninstall(_uninstallDir, removeSettings, Report));
                await Task.Delay(350);
                _busy = false;
                SetPage(Page.Removed);
            }
            catch (Exception e)
            {
                _busy = false;
                SetPage(Page.Uninstall);
                ShowError("Uninstall failed: " + e.Message);
            }
        }

        private void Failed(string message)
        {
            _busy = false;
            SetPage(Page.Install);
            ShowError(message);
        }

        private void Finish()
        {
            if (_launch.Checked && _installedDir != null)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(Path.Combine(_installedDir, Installer.ExeName))
                    {
                        WorkingDirectory = _installedDir,
                        UseShellExecute = true,
                    });
                }
                catch (Exception) { }
            }
            Close();
        }

        private void Report(float progress, string step)
        {
            if (IsDisposed) return;
            BeginInvoke(new Action(() =>
            {
                _progress = Math.Max(_progress, progress);
                _step = step;
            }));
        }

        private void Browse()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Choose where to install SPECTRM. A SPECTRM folder is created inside the one you pick.";
                dialog.ShowNewFolderButton = true;
                try
                {
                    var current = Installer.NormaliseDir(_pathText.Text);
                    var parent = Path.GetDirectoryName(current);
                    if (parent != null && Directory.Exists(parent)) dialog.SelectedPath = parent;
                }
                catch (Exception) { }
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    _pathText.Text = Installer.NormaliseDir(dialog.SelectedPath);
            }
        }

        private void ShowError(string message)
        {
            _error = message;
            Invalidate();
        }

        private void ClearError()
        {
            if (_error.Length == 0) return;
            _error = "";
            Invalidate();
        }

        private void Animate()
        {
            // Ease the bar towards the real progress and run a soft highlight along it.
            _shownProgress += (_progress - _shownProgress) * 0.18f;
            if (_progress - _shownProgress < 0.002f) _shownProgress = _progress;
            _shimmer = (_shimmer + 0.012f) % 1.4f;
            Invalidate(new Rectangle(0, D(150), ClientSize.Width, D(60)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // Brand strip along the top edge.
            var strip = new Rectangle(0, 0, ClientSize.Width, Math.Max(2, D(3)));
            using (var brush = new LinearGradientBrush(new Rectangle(0, 0, ClientSize.Width + 1, strip.Height), Theme.Accent, Theme.Highlight, 0f))
            {
                brush.InterpolationColors = new ColorBlend { Colors = new[] { Theme.Accent, Theme.Secondary, Theme.Highlight }, Positions = new[] { 0f, 0.55f, 1f } };
                g.FillRectangle(brush, strip);
            }

            using (var ink = new SolidBrush(Theme.Ink))
                g.FillPath(ink, _logo);
            var subtitle = _payload != null
                ? "SETUP  ·  VERSION " + _payload.Version + (_existingVersion != null ? "  ·  INSTALLED " + _existingVersion : "")
                : "UNINSTALLER";
            TextRenderer.DrawText(g, subtitle, _labelFont, new Point(D(Gutter), D(70)), Theme.Label, TextFormatFlags.NoPadding);

            var textLeft = D(Gutter);
            if (_page == Page.Done || _page == Page.Removed)
            {
                DrawCheck(g, new RectangleF(D(Gutter), D(107), D(26), D(26)), _page == Page.Done ? Theme.Accent : Theme.Secondary);
                textLeft = D(Gutter + 38);
            }
            TextRenderer.DrawText(g, _heading, _headingFont, new Point(textLeft - D(1), D(103)), Theme.Ink, TextFormatFlags.NoPadding);

            switch (_page)
            {
                case Page.Install:
                    TextRenderer.DrawText(g, "INSTALL TO", _labelFont, new Point(D(Gutter), D(146)), Theme.Label, TextFormatFlags.NoPadding);
                    break;
                case Page.Progress:
                    DrawProgress(g);
                    break;
                default:
                    TextRenderer.DrawText(g, _detail, _bodyFont, new Rectangle(textLeft, D(140), D(WindowWidth - Gutter) - textLeft, D(44)), Theme.Label,
                        TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
                    break;
            }

            if (_error.Length > 0)
                TextRenderer.DrawText(g, _error, _errorFont, new Rectangle(D(Gutter), D(272), D(ContentWidth), D(26)), Theme.Highlight,
                    TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);

            using (var pen = new Pen(Theme.Line))
                g.DrawLine(pen, 0, D(FooterTop), ClientSize.Width, D(FooterTop));
            var note = _page == Page.Install
                ? "No admin rights needed  ·  " + Math.Round(_payload.OriginalLength / 1048576.0) + " MB"
                : _page == Page.Progress ? "Please keep this window open" : "";
            TextRenderer.DrawText(g, note, _labelFont, new Rectangle(D(Gutter), D(FooterTop), _secondary.Left - D(Gutter + 12), D(WindowHeight - FooterTop)), Theme.Label,
                TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

            using (var border = new Pen(Theme.Line))
                g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }

        private void DrawProgress(Graphics g)
        {
            var bar = new RectangleF(D(Gutter), D(160), D(ContentWidth), D(8));
            using (var track = Theme.Rounded(bar, bar.Height / 2f))
            using (var brush = new SolidBrush(Theme.Track))
                g.FillPath(brush, track);

            var filled = new RectangleF(bar.X, bar.Y, Math.Max(bar.Height, bar.Width * _shownProgress), bar.Height);
            using (var path = Theme.Rounded(filled, bar.Height / 2f))
            {
                using (var brush = new LinearGradientBrush(new RectangleF(bar.X - 1, bar.Y, bar.Width + 2, bar.Height), Theme.Accent, Theme.Secondary, 0f))
                    g.FillPath(brush, path);
                // Moving highlight so the bar looks alive even while one step takes a while.
                var center = bar.X + (_shimmer - 0.2f) * bar.Width;
                var glow = new RectangleF(center - D(60), bar.Y, D(120), bar.Height);
                using (var brush = new LinearGradientBrush(glow, Color.FromArgb(0, Color.White), Color.FromArgb(0, Color.White), 0f))
                {
                    brush.InterpolationColors = new ColorBlend
                    {
                        Colors = new[] { Color.FromArgb(0, Color.White), Color.FromArgb(110, Color.White), Color.FromArgb(0, Color.White) },
                        Positions = new[] { 0f, 0.5f, 1f },
                    };
                    var clip = g.Clip;
                    g.SetClip(path);
                    g.FillRectangle(brush, glow);
                    g.Clip = clip;
                }
            }

            var percent = (int)Math.Round(_shownProgress * 100);
            TextRenderer.DrawText(g, _step + "…", _bodyFont, new Point(D(Gutter), D(180)), Theme.Label, TextFormatFlags.NoPadding);
            var percentText = percent + "%";
            var size = TextRenderer.MeasureText(g, percentText, _bodyFont, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, percentText, _bodyFont, new Point(D(WindowWidth - Gutter) - size.Width, D(180)), Theme.Ink, TextFormatFlags.NoPadding);
        }

        private static void DrawCheck(Graphics g, RectangleF circle, Color color)
        {
            using (var brush = new SolidBrush(color))
                g.FillEllipse(brush, circle);
            using (var pen = new Pen(Color.FromArgb(4, 12, 11), circle.Width * 0.11f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(pen, new[]
                {
                    new PointF(circle.X + circle.Width * 0.28f, circle.Y + circle.Height * 0.52f),
                    new PointF(circle.X + circle.Width * 0.44f, circle.Y + circle.Height * 0.68f),
                    new PointF(circle.X + circle.Width * 0.73f, circle.Y + circle.Height * 0.36f),
                });
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape && !_busy)
            {
                Close();
                return true;
            }
            if (keyData == Keys.Enter && !_busy && !(ActiveControl is FlatButton) && !(ActiveControl is ToggleRow))
            {
                _primary.PerformClick();
                return true;
            }
            if (keyData == Keys.Enter && ActiveControl is FlatButton button)
            {
                button.PerformClick();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // Start on the main button rather than in the path box (which would show the path all selected).
            if (_primary.Visible) ActiveControl = _primary;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_busy && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
            base.OnFormClosing(e);
        }

        // Borderless window: drag anywhere on the background, keep a drop shadow, rounded corners on Windows 11.
        protected override CreateParams CreateParams
        {
            get
            {
                const int CsDropShadow = 0x20000;
                var parameters = base.CreateParams;
                parameters.ClassStyle |= CsDropShadow;
                return parameters;
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            const int DwmwaWindowCornerPreference = 33, DwmwcpRound = 2;
            var round = DwmwcpRound;
            try { DwmSetWindowAttribute(Handle, DwmwaWindowCornerPreference, ref round, sizeof(int)); }
            catch (Exception) { }
        }

        protected override void WndProc(ref Message m)
        {
            const int WmNcHitTest = 0x84, HtClient = 1, HtCaption = 2;
            base.WndProc(ref m);
            if (m.Msg == WmNcHitTest && (int)m.Result == HtClient) m.Result = (IntPtr)HtCaption;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _animation.Dispose();
                _logo.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
