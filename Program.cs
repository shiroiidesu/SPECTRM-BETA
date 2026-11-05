using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Spectrm;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MeterWindow());
    }
}

internal enum ChartKind { Spectrogram, Waveform, LufsMeter, Vectorscope, VuMeter, Oscilloscope, Spectrum }

/// <summary>
/// SPECTRM typography: Windows 11's Segoe UI Variable (the "Small" cut is tuned for tiny labels),
/// falling back to classic Segoe UI on systems without it. GDI reports some long family names truncated, as below.
/// </summary>
internal static class UiFonts
{
    private static readonly HashSet<string> Installed = new(
        new InstalledFontCollection().Families.Select(family => family.Name), StringComparer.OrdinalIgnoreCase);

    private static string Pick(string preferred, string fallback) => Installed.Contains(preferred) ? preferred : fallback;

    public static readonly string Small = Pick("Segoe UI Variable Small", "Segoe UI");

    public static readonly string Text = Pick("Segoe UI Variable Text", "Segoe UI");
    public static readonly string Display = Pick("Segoe UI Variable Display", "Segoe UI");
    /// <summary>Heaviest weight (Black), used for numbers: scales, VU figures and readouts.</summary>
    public static readonly string Numbers = Pick("Segoe UI Black", "Segoe UI");

    /// <summary>Crisp, sub-pixel smoothed text; every surface is opaque black, so ClearType renders cleanly.</summary>
    public const TextRenderingHint Rendering = TextRenderingHint.ClearTypeGridFit;
}

/// <summary>SPECTRM colour palette: mint primary, violet secondary, hot-pink highlight on teal-tinted black.</summary>
internal static class Palette
{
    public static readonly Color Accent = Color.FromArgb(112, 240, 200);    // mint
    public static readonly Color Secondary = Color.FromArgb(150, 132, 255); // violet
    public static readonly Color Highlight = Color.FromArgb(255, 84, 140);  // hot pink
    public static readonly Color Needle = Accent;
    public static readonly Color Over = Highlight;
    public static readonly Color Ink = Color.FromArgb(228, 240, 236);
    public static readonly Color Label = Color.FromArgb(108, 136, 132);
    public static readonly Color Grid = Color.FromArgb(17, 33, 31);
    public static readonly Color Track = Color.FromArgb(10, 22, 21);
    public static readonly Color Box = Color.FromArgb(15, 38, 36);
    public static readonly Color Line = Color.FromArgb(26, 46, 44);
    public static readonly Color Hover = Color.FromArgb(18, 36, 34);
}

internal sealed class LowLatencyLoopbackCapture : WasapiCapture
{
    public LowLatencyLoopbackCapture(MMDevice outputDevice) : base(outputDevice, true, 20) { }

    protected override AudioClientStreamFlags GetAudioClientStreamFlags() => AudioClientStreamFlags.Loopback;
}

internal sealed class BufferedPanel : Panel
{
    public BufferedPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        UpdateStyles();
    }
}

internal sealed class BufferedTableLayoutPanel : TableLayoutPanel
{
    public BufferedTableLayoutPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        UpdateStyles();
    }

    protected override void WndProc(ref Message m)
    {
        if (EdgePassThrough.Handle(this, ref m)) return;
        base.WndProc(ref m);
    }
}

/// <summary>
/// The meters fill the window edge to edge, so hit-tests near the outer edge are passed
/// through (HTTRANSPARENT) to the window, which turns them into resize handles.
/// </summary>
internal static class EdgePassThrough
{
    private const int WmNcHitTest = 0x84;
    private const int HtTransparent = -1;
    public const int EdgeSize = 6;

    public static bool Handle(Control control, ref Message m)
    {
        if (m.Msg != WmNcHitTest || control.FindForm() is not { WindowState: FormWindowState.Normal } form) return false;
        var packed = m.LParam.ToInt64();
        var point = form.PointToClient(new Point(unchecked((short)(packed & 0xFFFF)), unchecked((short)((packed >> 16) & 0xFFFF))));
        if (point.X >= EdgeSize && point.Y >= EdgeSize && point.X < form.ClientSize.Width - EdgeSize && point.Y < form.ClientSize.Height - EdgeSize)
            return false;
        m.Result = (IntPtr)HtTransparent;
        return true;
    }
}

internal sealed class MeterWindow : Form
{
    private const int WmNcHitTest = 0x84;
    private const int WmNcCalcSize = 0x83;
    private const int WmNcActivate = 0x86;
    private const int WmNcPaint = 0x85;
    private const int WmNcLButtonDown = 0xA1;
    private const int WsSysMenu = 0x00080000;
    private const int WsThickFrame = 0x00040000;
    private const int WsMinimizeBox = 0x00020000;
    private const int WsMaximizeBox = 0x00010000;
    private const int HtCaption = 2;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;

    private static readonly Color Background = Color.Black;
    private static readonly Color Surface = Color.FromArgb(14, 14, 17);
    private static readonly Color Border = Color.FromArgb(39, 38, 45);
    private static readonly Color Ink = Palette.Ink;
    private static readonly Color Muted = Palette.Label;
    private static readonly Color Purple = Color.FromArgb(174, 143, 226);
    private static readonly Color Mint = Color.FromArgb(207, 190, 239);
    private static readonly Color Orange = Color.FromArgb(174, 143, 226);
    private static readonly Color Blue = Color.FromArgb(224, 219, 232);

    private readonly AnalysisEngine _analysis = new();
    private readonly Label _status = new();
    private readonly List<MeterChart> _charts = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer _deviceTimer = new() { Interval = 1500 };
    private readonly System.Windows.Forms.Timer _topBarTimer = new() { Interval = 30 };
    private readonly BufferedPanel _topBar = new();
    private readonly Button _settingsButton = new();
    private readonly Button _pinButton = new();
    private readonly Button _layoutButton = new();
    private BufferedTableLayoutPanel? _chartGrid;
    private bool _vertical;
    private bool _pinned;
    private Size? _horizontalSize;
    private Size? _verticalSize;
    private static readonly Size HorizontalMinimum = new(900, 100);
    private static readonly Size VerticalMinimum = new(320, 640);
    private readonly Button _minimizeButton = new();
    private readonly Button _maximizeButton = new();
    private readonly Button _closeButton = new();
    private readonly ToolTip _statusTooltip = new();
    private AnalysisSnapshot _snapshot = new();
    private int _targetFps;
    private long _lastDisplayedGeneration = -1;
    private DateTime _lastIdleRefresh = DateTime.MinValue;
    private DateTime _topBarHideAt = DateTime.MinValue;
    private bool _timerResolutionActive;
    private bool _isWindowDragging;
    private bool _ignoreTopRevealUntilPointerLeaves;

    private const int TopMenuRevealHeight = 8;
    private const int TopBarHeight = 30;
    private readonly SettingsOverlay _settingsOverlay = new();
    private StartupSplash? _splash;
    private Rectangle? _boundsBeforeSettings;
    private Size _minimumSizeBeforeSettings;
    private const int TopMenuHideDelayMilliseconds = 700;

    private WasapiCapture? _outputCapture;
    private MMDeviceEnumerator? _deviceEnumerator;
    private MMDevice? _outputDevice;
    private string? _outputDeviceId;
    private string? _lastOutputError;
    private bool _disposed;

    public MeterWindow()
    {
        Text = "SPECTRM";
        Icon = LoadAppIcon();
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        LoadSettings();
        MinimumSize = _vertical ? VerticalMinimum : HorizontalMinimum;
        Size = _vertical ? DefaultVerticalSize() : DefaultHorizontalSize();
        BackColor = Background;
        ForeColor = Ink;
        Font = new Font(UiFonts.Text, 9F, FontStyle.Bold);
        DoubleBuffered = true;
        KeyPreview = true;
        StyleTooltips();
        BuildInterface();
        ConfigureFrameRate();
        TopMost = _pinned;
        UpdatePinButton();
        UpdateLayoutButton();
        _settingsOverlay.Pinned = _pinned;
        _timer.Tick += (_, _) => RefreshMeters();
        _deviceTimer.Tick += (_, _) => FollowDefaultOutput();
        _topBarTimer.Tick += (_, _) => UpdateTopBarVisibility();
        _timer.Start();
        _deviceTimer.Start();
        _topBarTimer.Start();
        FormClosing += (_, _) => DisposeAudio();
        Shown += (_, _) => StartDefaultOutput();
        AddStartupSplash();
    }

    private void AddStartupSplash()
    {
        _splash = new StartupSplash { Bounds = ClientRectangle };
        Controls.Add(_splash);
        _splash.BringToFront();
        Resize += (_, _) => { if (_splash is not null) _splash.Bounds = ClientRectangle; };
        Shown += (_, _) => _splash?.Start();
        _splash.Finished += (_, _) =>
        {
            if (_splash is null) return;
            Controls.Remove(_splash);
            _splash.Dispose();
            _splash = null;
        };
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyBorderlessDwmStyle();
        // Make sure Windows re-asks for the frame size, so no native frame strip survives startup.
        const uint SwpNoSize = 0x1, SwpNoMove = 0x2, SwpNoZOrder = 0x4, SwpNoActivate = 0x10, SwpFrameChanged = 0x20;
        SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.Style |= WsSysMenu | WsThickFrame | WsMinimizeBox | WsMaximizeBox;
            return parameters;
        }
    }

    private void BuildInterface()
    {
        var root = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Background,
            ColumnCount = 1,
            RowCount = 1,
            Padding = Padding.Empty,
        };
        Controls.Add(root);

        root.Controls.Add(BuildCharts(), 0, 0);
        BuildTopBar();
        BuildSettingsOverlay();
    }

    private void BuildTopBar()
    {
        _topBar.Visible = false;
        _topBar.BackColor = Background;
        _topBar.Size = new Size(ClientSize.Width, TopBarHeight);
        _topBar.Location = new Point(0, -TopBarHeight); // tucked above the window until it glides down
        _topBar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _topBar.Paint += (_, e) => PaintTopBar(e.Graphics);

        _status.Text = "● OFFLINE";
        _status.AutoSize = true;
        _status.ForeColor = Muted;
        _status.Font = new Font(UiFonts.Small, 7.5F, FontStyle.Bold);
        _status.BackColor = Background;
        _status.Cursor = Cursors.SizeAll;

        // Tool buttons: layout toggle, pin (always on top), options cog.
        ConfigureWindowButton(_settingsButton, "", "Options (Alt+S)");
        _settingsButton.AccessibleDescription = "Opens options: frame rate, layout and always on top. Keyboard shortcut Alt+S.";
        _settingsButton.Click += (_, _) => OpenSettings();

        ConfigureWindowButton(_pinButton, "", "Pin: always stay on top (Alt+P)");
        _pinButton.Click += (_, _) => SetPinned(!_pinned);

        ConfigureWindowButton(_layoutButton, "", "Switch layout (Alt+L)");
        _layoutButton.Paint += (_, e) => PaintLayoutIcon(e.Graphics);
        _layoutButton.Click += (_, _) => SetVertical(!_vertical);

        foreach (var button in new[] { _layoutButton, _pinButton, _settingsButton })
        {
            button.Size = new Size(34, TopBarHeight);
            button.Top = 0;
            button.Font = new Font("Segoe MDL2 Assets", 9F);
        }

        ConfigureWindowButton(_minimizeButton, "", "Minimize");
        ConfigureWindowButton(_maximizeButton, "", "Maximize");
        ConfigureWindowButton(_closeButton, "", "Close");
        foreach (var button in new[] { _minimizeButton, _maximizeButton, _closeButton })
        {
            button.Size = new Size(44, TopBarHeight);
            button.Top = 0;
            button.Font = new Font("Segoe MDL2 Assets", 7.5F);
        }
        _closeButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(196, 43, 28);
        _closeButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(160, 34, 22);
        _minimizeButton.Click += (_, _) => WindowState = FormWindowState.Minimized;
        _maximizeButton.Click += (_, _) => ToggleMaximize();
        _closeButton.Click += (_, _) => Close();

        _topBar.Resize += (_, _) => LayoutTopBar();
        _status.SizeChanged += (_, _) => LayoutTopBar();

        _topBar.Controls.Add(_status);
        _topBar.Controls.Add(_layoutButton);
        _topBar.Controls.Add(_pinButton);
        _topBar.Controls.Add(_settingsButton);
        _topBar.Controls.Add(_minimizeButton);
        _topBar.Controls.Add(_maximizeButton);
        _topBar.Controls.Add(_closeButton);
        Controls.Add(_topBar);
        _topBar.BringToFront();
        _glideTimer.Tick += (_, _) => GlideStep();

        _status.MouseDown += (_, e) => BeginWindowDrag(e);
        _topBar.MouseDown += (_, e) => BeginWindowDrag(e);
        Resize += (_, _) => UpdateMaximizeButton();
    }

    private static readonly Font TooltipFont = new(UiFonts.Small, 8F, FontStyle.Bold);

    /// <summary>Hover tooltips drawn in SPECTRM's own style: bold text on a dark teal box with a hairline border.</summary>
    private void StyleTooltips()
    {
        _statusTooltip.OwnerDraw = true;
        _statusTooltip.Popup += (_, e) =>
        {
            var text = _statusTooltip.GetToolTip(e.AssociatedControl);
            var size = TextRenderer.MeasureText(text, TooltipFont);
            e.ToolTipSize = new Size(size.Width + 12, size.Height + 8);
        };
        _statusTooltip.Draw += (_, e) =>
        {
            using (var fill = new SolidBrush(Palette.Box))
                e.Graphics.FillRectangle(fill, e.Bounds);
            using (var border = new Pen(Palette.Line))
                e.Graphics.DrawRectangle(border, e.Bounds.X, e.Bounds.Y, e.Bounds.Width - 1, e.Bounds.Height - 1);
            TextRenderer.DrawText(e.Graphics, e.ToolTipText, TooltipFont, e.Bounds, Palette.Ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        };
    }

    private void LayoutTopBar()
    {
        // Window controls are narrower in the slim vertical layout so everything fits.
        var windowButtonWidth = _topBar.ClientSize.Width < 420 ? 34 : 44;
        foreach (var button in new[] { _minimizeButton, _maximizeButton, _closeButton })
            button.Width = windowButtonWidth;
        _closeButton.Left = _topBar.ClientSize.Width - _closeButton.Width;
        _maximizeButton.Left = _closeButton.Left - _maximizeButton.Width;
        _minimizeButton.Left = _maximizeButton.Left - _minimizeButton.Width;
        _settingsButton.Left = _minimizeButton.Left - 8 - _settingsButton.Width;
        _pinButton.Left = _settingsButton.Left - _pinButton.Width;
        _layoutButton.Left = _pinButton.Left - _layoutButton.Width;
        _status.Left = _layoutButton.Left - _status.Width - 10;
        _status.Top = (TopBarHeight - _status.Height) / 2;
        // Hide the status text rather than let it run into the logo.
        _status.Visible = _status.Left > TopBarLogoRight + 16;
    }

    private void PaintTopBar(Graphics graphics)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = UiFonts.Rendering;
        using var hairline = new Pen(Palette.Line);
        graphics.DrawLine(hairline, 0, _topBar.Height - 1, _topBar.Width, _topBar.Height - 1);

        // Logo: the SPECTRM wordmark and bar mark, drawn from the vector paths in "SPECTRM logo.svg".
        // The bar mark is drawn on whole pixels so its 1 px bars stay crisp at this size.
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using var ink = new SolidBrush(Ink);
        graphics.FillPath(ink, TopBarWordmark);
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        LogoArt.DrawPixelMark(graphics, ink, TopBarMarkLeft, LogoTop, LogoHeight, 1, 1, LogoHeight);
    }

    private const int LogoLeft = 14;
    private const int LogoHeight = 12;
    private const int LogoTop = (TopBarHeight - LogoHeight) / 2;
    private static readonly int TopBarMarkLeft;
    private static readonly int TopBarLogoRight;

    /// <summary>The wordmark path, already scaled and placed for the top bar.</summary>
    private static readonly GraphicsPath TopBarWordmark = CreateTopBarWordmark(out TopBarMarkLeft, out TopBarLogoRight);

    private static GraphicsPath CreateTopBarWordmark(out int markLeft, out int logoRight)
    {
        using var full = LogoArt.CreateFull();
        using var mark = LogoArt.CreateMark();
        var fullBounds = full.GetBounds();
        var markBounds = mark.GetBounds();
        var scale = LogoHeight / fullBounds.Height;
        markLeft = (int)Math.Round(LogoLeft + (markBounds.X - fullBounds.X) * scale);
        logoRight = markLeft + LogoArt.MarkWidth(1, 1);

        var wordmark = LogoArt.CreateWordmark();
        using var matrix = new Matrix();
        matrix.Translate(LogoLeft, LogoTop);
        matrix.Scale(scale, scale);
        matrix.Translate(-fullBounds.X, -fullBounds.Y);
        wordmark.Transform(matrix);
        return wordmark;
    }

    private static Icon? LoadAppIcon()
    {
        using var stream = typeof(MeterWindow).Assembly.GetManifestResourceStream("SPECTRM.ico");
        return stream is null ? null : new Icon(stream);
    }

    private void ConfigureWindowButton(Button button, string text, string tooltip)
    {
        button.Text = text;
        button.Size = new Size(44, TopBarHeight);
        button.Top = 0;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.BorderColor = Background;
        button.FlatAppearance.MouseOverBackColor = Palette.Hover;
        button.FlatAppearance.MouseDownBackColor = Palette.Line;
        button.BackColor = Background;
        button.ForeColor = Palette.Ink;
        button.Font = new Font(UiFonts.Text, 12F);
        button.Cursor = Cursors.Hand;
        button.TabStop = true;
        button.AccessibleName = tooltip;
        button.AccessibleRole = AccessibleRole.PushButton;
        _statusTooltip.SetToolTip(button, tooltip);

        // A mouse click leaves keyboard focus on the button, and the bar stays down while it holds focus, so it
        // would never slide away. Drop focus after mouse clicks; keyboard users keep focus to tab between buttons.
        var clickedByMouse = false;
        button.MouseDown += (_, _) => clickedByMouse = true;
        button.Click += (_, _) =>
        {
            if (!clickedByMouse) return;
            clickedByMouse = false;
            BeginInvoke(() => { if (!IsDisposed && _topBar.ContainsFocus) ActiveControl = null; });
        };
    }

    private void UpdateTopBarVisibility()
    {
        if (WindowState == FormWindowState.Minimized || IsDisposed || _isWindowDragging) return;
        if (_settingsOverlay.Visible || _splash is not null)
        {
            if (_topBar.Visible) HideTopBarNow();
            return;
        }
        var cursor = PointToClient(Cursor.Position);
        var overRevealArea = ClientRectangle.Contains(cursor) && cursor.Y <= TopMenuRevealHeight;
        var overTopBar = _topBarShown && new Rectangle(0, 0, ClientSize.Width, TopBarHeight).Contains(cursor);

        if (_ignoreTopRevealUntilPointerLeaves)
        {
            if (!overRevealArea)
                _ignoreTopRevealUntilPointerLeaves = false;
            else
                return;
        }

        if (overRevealArea)
        {
            ShowTopBar();
            return;
        }

        if (!_topBarShown || overTopBar || _topBar.ContainsFocus) return;
        if (_topBarHideAt == DateTime.MinValue)
            _topBarHideAt = DateTime.UtcNow.AddMilliseconds(TopMenuHideDelayMilliseconds);
        else if (DateTime.UtcNow >= _topBarHideAt)
            HideTopBar();
    }

    private void ShowTopBar()
    {
        _ignoreTopRevealUntilPointerLeaves = false;
        _topBarHideAt = DateTime.MinValue;
        GlideTopBar(true);
    }

    private void HideTopBar()
    {
        var cursor = PointToClient(Cursor.Position);
        _ignoreTopRevealUntilPointerLeaves = ClientRectangle.Contains(cursor) && cursor.Y <= TopMenuRevealHeight;
        _topBarHideAt = DateTime.MinValue;
        GlideTopBar(false);
    }

    // The top bar glides down from above the window when shown and slides back up when hidden.
    private const int GlideInMilliseconds = 200;
    private const int GlideOutMilliseconds = 160;
    private readonly System.Windows.Forms.Timer _glideTimer = new() { Interval = 10 };
    private readonly System.Diagnostics.Stopwatch _glideClock = new();
    private bool _topBarShown;
    private float _glideFrom;
    private float _glideValue; // 0 = tucked away above the window, 1 = fully down

    private void GlideTopBar(bool show)
    {
        if (_topBarShown == show) return;
        _topBarShown = show;
        _glideFrom = _glideValue;
        _glideClock.Restart();
        if (show)
        {
            _topBar.Visible = true;
            _topBar.BringToFront();
        }
        _glideTimer.Start();
        GlideStep();
    }

    private void GlideStep()
    {
        var target = _topBarShown ? 1f : 0f;
        var duration = (_topBarShown ? GlideInMilliseconds : GlideOutMilliseconds) * Math.Abs(target - _glideFrom);
        var p = duration <= 0 ? 1f : Math.Min(1f, _glideClock.ElapsedMilliseconds / duration);
        // Decelerate into place coming down; accelerate away going up.
        var eased = _topBarShown ? 1f - MathF.Pow(1f - p, 3f) : p * p * p;
        _glideValue = _glideFrom + (target - _glideFrom) * eased;
        _topBar.Top = (int)Math.Round(-TopBarHeight * (1f - _glideValue));
        if (p < 1f) return;
        _glideTimer.Stop();
        if (!_topBarShown) _topBar.Visible = false;
    }

    private void HideTopBarNow()
    {
        _glideTimer.Stop();
        _topBarShown = false;
        _glideValue = 0f;
        _topBarHideAt = DateTime.MinValue;
        _topBar.Visible = false;
        _topBar.Top = -TopBarHeight;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_splash is { Running: true })
        {
            _splash.Skip();
            if (keyData == Keys.Escape) return true;
        }

        if (keyData == (Keys.Control | Keys.Shift | Keys.M))
        {
            if (_topBarShown) HideTopBar();
            else ShowTopBar();
            return true;
        }

        if (keyData == (Keys.Alt | Keys.S))
        {
            OpenSettings();
            return true;
        }

        if (keyData == (Keys.Alt | Keys.P))
        {
            SetPinned(!_pinned);
            return true;
        }

        if (keyData == (Keys.Alt | Keys.L))
        {
            SetVertical(!_vertical);
            return true;
        }

        if (keyData == Keys.Escape && _settingsOverlay.Visible)
        {
            CloseSettings();
            return true;
        }

        if (keyData == Keys.Escape && _topBarShown)
        {
            HideTopBar();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private void ApplyBorderlessDwmStyle()
    {
        if (!IsHandleCreated) return;
        // Windows 11 draws a 1 px outline (white/accent when focused) around every window; turn it off.
        const int DwmwaBorderColor = 34;
        var noColor = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        DwmSetWindowAttribute(Handle, DwmwaBorderColor, ref noColor, sizeof(int));
        // Square corners, so the strip fills its rectangle edge to edge.
        const int DwmwaWindowCornerPreference = 33;
        var doNotRound = 1; // DWMWCP_DONOTROUND
        DwmSetWindowAttribute(Handle, DwmwaWindowCornerPreference, ref doNotRound, sizeof(int));
    }

    protected override void WndProc(ref Message m)
    {
        // When focus changes, Windows repaints the (hidden) frame edge in its active/inactive colour.
        // Answer for it without letting the default frame painting run, and re-assert the no-border style.
        if (m.Msg == WmNcActivate)
        {
            ApplyBorderlessDwmStyle();
            m.Result = (IntPtr)1;
            return;
        }

        if (m.Msg == WmNcPaint)
        {
            m.Result = IntPtr.Zero;
            return;
        }

        // Make the whole window client area so Windows doesn't paint its light sizing strip along the top edge.
        if (m.Msg == WmNcCalcSize && m.WParam != IntPtr.Zero)
        {
            if (WindowState == FormWindowState.Maximized)
            {
                // A maximized window extends past the screen by the frame size; pull it back in.
                const int SmCxSizeFrame = 32, SmCySizeFrame = 33, SmCxPaddedBorder = 92;
                var padded = GetSystemMetrics(SmCxPaddedBorder);
                var rect = Marshal.PtrToStructure<NativeRect>(m.LParam);
                rect.Left += GetSystemMetrics(SmCxSizeFrame) + padded;
                rect.Right -= GetSystemMetrics(SmCxSizeFrame) + padded;
                rect.Top += GetSystemMetrics(SmCySizeFrame) + padded;
                rect.Bottom -= GetSystemMetrics(SmCySizeFrame) + padded;
                Marshal.StructureToPtr(rect, m.LParam, false);
            }
            m.Result = IntPtr.Zero;
            return;
        }

        if (m.Msg == WmNcHitTest)
        {
            var packedPosition = m.LParam.ToInt64();
            var screenPoint = new Point(
                unchecked((short)(packedPosition & 0xFFFF)),
                unchecked((short)((packedPosition >> 16) & 0xFFFF)));
            var point = PointToClient(screenPoint);
            if (WindowState == FormWindowState.Normal)
            {
                const int resizeBorder = EdgePassThrough.EdgeSize;
                var left = point.X < resizeBorder;
                var right = point.X >= ClientSize.Width - resizeBorder;
                var top = point.Y < resizeBorder;
                var bottom = point.Y >= ClientSize.Height - resizeBorder;
                if (top && left) { m.Result = (IntPtr)HtTopLeft; return; }
                if (top && right) { m.Result = (IntPtr)HtTopRight; return; }
                if (bottom && left) { m.Result = (IntPtr)HtBottomLeft; return; }
                if (bottom && right) { m.Result = (IntPtr)HtBottomRight; return; }
                if (left) { m.Result = (IntPtr)HtLeft; return; }
                if (right) { m.Result = (IntPtr)HtRight; return; }
                if (top) { m.Result = (IntPtr)HtTop; return; }
                if (bottom) { m.Result = (IntPtr)HtBottom; return; }
            }

            if (_topBar.Visible && _topBar.Bounds.Contains(point))
            {
                var barPoint = _topBar.PointToClient(screenPoint);
                if (_topBar.GetChildAtPoint(barPoint) is not Button)
                {
                    m.Result = (IntPtr)HtCaption;
                    return;
                }
            }
        }
        base.WndProc(ref m);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

    private void ToggleMaximize()
    {
        WindowState = WindowState == FormWindowState.Maximized
            ? FormWindowState.Normal
            : FormWindowState.Maximized;
        UpdateMaximizeButton();
    }

    private void UpdateMaximizeButton()
    {
        _maximizeButton.Text = WindowState == FormWindowState.Maximized ? "" : "";
        _statusTooltip.SetToolTip(_maximizeButton, WindowState == FormWindowState.Maximized ? "Restore" : "Maximize");
    }

    private void BeginWindowDrag(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || WindowState == FormWindowState.Maximized) return;
        _isWindowDragging = true;
        try
        {
            ReleaseCapture();
            SendMessage(Handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
        }
        finally
        {
            _isWindowDragging = false;
        }
    }

    // Share of the strip each meter gets, in display order, for each layout.
    private static readonly float[] HorizontalWeights = { 18F, 12.5F, 6.5F, 13F, 16F, 10F, 24F };
    private static readonly float[] VerticalWeights = { 16F, 11F, 9F, 16F, 18F, 11F, 19F };

    private Control BuildCharts()
    {
        _chartGrid = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Background,
            Padding = Padding.Empty,
        };
        foreach (var kind in new[] { ChartKind.Spectrogram, ChartKind.Waveform, ChartKind.LufsMeter, ChartKind.Vectorscope, ChartKind.VuMeter, ChartKind.Oscilloscope, ChartKind.Spectrum })
        {
            var chart = new MeterChart(kind) { Dock = DockStyle.Fill, Margin = Padding.Empty };
            chart.MouseDown += (_, e) => BeginWindowDrag(e);
            _charts.Add(chart);
        }
        ApplyChartLayout();
        return _chartGrid;
    }

    /// <summary>Places the meters in one row (horizontal) or one column (vertical).</summary>
    private void ApplyChartLayout()
    {
        if (_chartGrid is null) return;
        var grid = _chartGrid;
        var weights = _vertical ? VerticalWeights : HorizontalWeights;
        grid.SuspendLayout();
        grid.Controls.Clear();
        grid.ColumnStyles.Clear();
        grid.RowStyles.Clear();
        grid.ColumnCount = _vertical ? 1 : _charts.Count;
        grid.RowCount = _vertical ? _charts.Count : 1;
        if (_vertical)
        {
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (var weight in weights) grid.RowStyles.Add(new RowStyle(SizeType.Percent, weight));
        }
        else
        {
            foreach (var weight in weights) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, weight));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        }
        for (var i = 0; i < _charts.Count; i++)
            grid.Controls.Add(_charts[i], _vertical ? 0 : i, _vertical ? i : 0);
        grid.ResumeLayout(true);
    }

    private static Size DefaultHorizontalSize()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1040);
        return new Size(Math.Max(HorizontalMinimum.Width, Math.Min(1800, area.Width - 60)), 150);
    }

    private static Size DefaultVerticalSize()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1040);
        return new Size(360, Math.Max(VerticalMinimum.Height, Math.Min(1000, area.Height - 60)));
    }

    private void SetVertical(bool vertical)
    {
        if (vertical == _vertical) return;
        CloseSettings();
        if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;

        // Remember this layout's size, then switch to the other layout's remembered (or default) size.
        if (_vertical) _verticalSize = Size; else _horizontalSize = Size;
        _vertical = vertical;
        var target = _vertical ? _verticalSize ?? DefaultVerticalSize() : _horizontalSize ?? DefaultHorizontalSize();

        var area = Screen.FromControl(this).WorkingArea;
        target = new Size(Math.Min(target.Width, area.Width), Math.Min(target.Height, area.Height));
        // Keep the top-left corner where it is, nudged back on screen if the new shape would spill off.
        var x = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - target.Width));
        var y = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - target.Height));

        SuspendLayout();
        MinimumSize = Size.Empty;
        Bounds = new Rectangle(x, y, target.Width, target.Height);
        MinimumSize = _vertical ? VerticalMinimum : HorizontalMinimum;
        ApplyChartLayout();
        ResumeLayout(true);
        UpdateLayoutButton();
        SaveSettings();
    }

    private void SetPinned(bool pinned)
    {
        _pinned = pinned;
        TopMost = pinned;
        UpdatePinButton();
        _settingsOverlay.Pinned = pinned;
        SaveSettings();
    }

    private void UpdatePinButton()
    {
        _pinButton.Text = _pinned ? "" : "";
        _pinButton.ForeColor = _pinned ? Palette.Accent : Palette.Ink;
        var tip = _pinned ? "Unpin: stop staying on top (Alt+P)" : "Pin: always stay on top (Alt+P)";
        _statusTooltip.SetToolTip(_pinButton, tip);
        _pinButton.AccessibleName = tip;
    }

    private void UpdateLayoutButton()
    {
        var tip = _vertical ? "Switch to horizontal layout (Alt+L)" : "Switch to vertical layout (Alt+L)";
        _statusTooltip.SetToolTip(_layoutButton, tip);
        _layoutButton.AccessibleName = tip;
        _layoutButton.Invalidate();
        _settingsOverlay.Vertical = _vertical;
    }

    /// <summary>Draws the layout icon: the strip it switches to (a row of bars, or a column of bars).</summary>
    private void PaintLayoutIcon(Graphics graphics)
    {
        graphics.SmoothingMode = SmoothingMode.None;
        using var brush = new SolidBrush(_layoutButton.ForeColor);
        var cx = _layoutButton.Width / 2;
        var cy = _layoutButton.Height / 2;
        if (_vertical)
        {
            // Currently vertical -> icon shows a horizontal strip of three cells.
            for (var i = 0; i < 3; i++) graphics.FillRectangle(brush, cx - 8 + i * 6, cy - 3, 4, 6);
        }
        else
        {
            for (var i = 0; i < 3; i++) graphics.FillRectangle(brush, cx - 3, cy - 8 + i * 6, 6, 4);
        }
    }

    private static void DrawBorder(Graphics graphics, Rectangle bounds, Color color, int radius)
    {
        bounds.Width -= 1;
        bounds.Height -= 1;
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        using var pen = new Pen(color);
        graphics.DrawPath(pen, path);
    }

    /// <summary>
    /// Reads settings.txt: "key=value" lines (fps, layout, pinned). A file holding only a number is the
    /// older single-setting format and is read as the frame rate.
    /// </summary>
    private void LoadSettings()
    {
        _targetFps = 60;
        _vertical = false;
        _pinned = false;
        var path = GetSettingsPath();
        if (!File.Exists(path))
        {
            // Carry over the frame rate saved before the app was renamed from Sonora.
            var legacyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sonora", "settings.txt");
            if (!File.Exists(legacyPath)) return;
            path = legacyPath;
        }
        try
        {
            var invalid = new List<string>();
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                var separator = line.IndexOf('=');
                var key = separator < 0 ? "fps" : line[..separator].Trim().ToLowerInvariant();
                var value = (separator < 0 ? line : line[(separator + 1)..]).Trim().ToLowerInvariant();
                switch (key)
                {
                    case "fps" when value == "unlimited": _targetFps = 0; break;
                    case "fps" when int.TryParse(value, out var fps) && fps is 30 or 60 or 120: _targetFps = fps; break;
                    case "layout" when value is "horizontal" or "vertical": _vertical = value == "vertical"; break;
                    case "pinned" when value is "true" or "false": _pinned = value == "true"; break;
                    default: invalid.Add(line); break;
                }
            }
            if (invalid.Count > 0)
                MessageBox.Show($"Some saved settings could not be read and were reset to defaults:\n\n{string.Join("\n", invalid)}\n\nSettings file: {path}", "SPECTRM settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (IOException exception)
        {
            MessageBox.Show($"Could not read the saved settings. SPECTRM will use its defaults.\n\n{exception.Message}", "SPECTRM settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (UnauthorizedAccessException exception)
        {
            MessageBox.Show($"Could not read the saved settings. SPECTRM will use its defaults.\n\n{exception.Message}", "SPECTRM settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void SaveSettings()
    {
        try
        {
            var path = GetSettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, new[]
            {
                $"fps={(_targetFps == 0 ? "unlimited" : _targetFps.ToString())}",
                $"layout={(_vertical ? "vertical" : "horizontal")}",
                $"pinned={(_pinned ? "true" : "false")}",
            });
        }
        catch (IOException exception)
        {
            ShowError($"Could not save your settings. They will apply until SPECTRM closes.\n\n{exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            ShowError($"Could not save your settings. They will apply until SPECTRM closes.\n\n{exception.Message}");
        }
    }

    private static string GetSettingsPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SPECTRM", "settings.txt");

    private void SetTargetFps(int fps)
    {
        if (fps is not (0 or 30 or 60 or 120)) return;
        _targetFps = fps;
        ConfigureFrameRate();
        SaveSettings();
    }

    private void ConfigureFrameRate()
    {
        _timer.Interval = _targetFps switch { 30 => 33, 60 => 16, 120 => 8, _ => 1 };
        var frameRate = _targetFps == 0 ? "Unlimited" : $"{_targetFps}";
        _settingsButton.AccessibleName = $"Options, frame rate currently {frameRate} FPS";
        _statusTooltip.SetToolTip(_settingsButton, $"Options · {frameRate} FPS (Alt+S)");
        if (_targetFps != 30 && !_timerResolutionActive)
            _timerResolutionActive = timeBeginPeriod(1) == 0;
        else if (_targetFps == 30 && _timerResolutionActive)
        {
            timeEndPeriod(1);
            _timerResolutionActive = false;
        }

    }

    private void BuildSettingsOverlay()
    {
        _settingsOverlay.Bounds = ClientRectangle;
        Resize += (_, _) => _settingsOverlay.Bounds = ClientRectangle;
        _settingsOverlay.FpsChosen += fps =>
        {
            SetTargetFps(fps);
            CloseSettings();
        };
        _settingsOverlay.LayoutChosen += vertical =>
        {
            CloseSettings();
            SetVertical(vertical);
        };
        _settingsOverlay.PinToggled += () => SetPinned(!_pinned);
        _settingsOverlay.CloseRequested += CloseSettings;
        _settingsOverlay.MouseDown += (_, e) =>
        {
            if (!_settingsOverlay.IsInteractive(e.Location)) BeginWindowDrag(e);
        };
        Controls.Add(_settingsOverlay);
    }

    private void OpenSettings()
    {
        if (_settingsOverlay.Visible)
        {
            CloseSettings();
            return;
        }
        EnsureRoomForSettings();
        _settingsOverlay.CurrentFps = _targetFps;
        HideTopBarNow();
        _settingsOverlay.Visible = true;
        _settingsOverlay.BringToFront();
        _settingsOverlay.Focus();
    }

    private void EnsureRoomForSettings()
    {
        _boundsBeforeSettings = null;
        _minimumSizeBeforeSettings = MinimumSize;
        var original = Bounds;
        var frameWidth = Width - ClientSize.Width;
        var frameHeight = Height - ClientSize.Height;
        // The panel lays itself out as a wide strip or a tall column; ask for room for the one that suits this layout.
        var panel = _vertical ? SettingsOverlay.TallMinimum : SettingsOverlay.WideMinimum;
        var needed = new Size(panel.Width + frameWidth, panel.Height + frameHeight);
        var tooSmall = WindowState == FormWindowState.Normal && (original.Width < needed.Width || original.Height < needed.Height);
        if (tooSmall)
        {
            _boundsBeforeSettings = original;
            var area = Screen.FromControl(this).WorkingArea;
            var width = Math.Max(original.Width, needed.Width);
            var height = Math.Max(original.Height, needed.Height);
            var x = Math.Clamp(original.Left - (width - original.Width) / 2, area.Left, Math.Max(area.Left, area.Right - width));
            var y = Math.Clamp(original.Top - (height - original.Height) / 2, area.Top, Math.Max(area.Top, area.Bottom - height));
            Bounds = new Rectangle(x, y, width, height);
        }
        // Keep the user from shrinking the window below the panel while it is open.
        MinimumSize = new Size(Math.Max(MinimumSize.Width, needed.Width), Math.Max(MinimumSize.Height, needed.Height));
    }

    private void CloseSettings()
    {
        if (!_settingsOverlay.Visible) return;
        _settingsOverlay.Visible = false;
        MinimumSize = _minimumSizeBeforeSettings;
        if (_boundsBeforeSettings is { } previous && WindowState == FormWindowState.Normal)
            Bounds = previous;
        _boundsBeforeSettings = null;
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint timeEndPeriod(uint period);

    private void FollowDefaultOutput()
    {
        if (_disposed) return;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (_outputCapture is null)
                StartDefaultOutput();
            else if (!string.Equals(device.ID, _outputDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                StopDefaultOutput();
                StartDefaultOutput();
            }
        }
        catch (Exception exception)
        {
            if (_outputCapture is not null) StopDefaultOutput();
            _lastOutputError = exception.Message;
            SetStatus($"DEFAULT OUTPUT UNAVAILABLE: {exception.Message}");
        }
    }

    private void StartDefaultOutput()
    {
        if (_disposed || _outputCapture is not null) return;
        WasapiCapture? capture = null;
        try
        {
            _deviceEnumerator = new MMDeviceEnumerator();
            _outputDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            capture = new LowLatencyLoopbackCapture(_outputDevice);
            var waveFormat = capture.WaveFormat;
            var captureSamples = new float[Math.Max(4096, waveFormat.SampleRate * waveFormat.Channels / 10)];
            _analysis.Reset();
            capture.DataAvailable += (_, args) =>
            {
                try
                {
                    var requiredSamples = args.BytesRecorded / (waveFormat.BitsPerSample / 8);
                    if (requiredSamples > captureSamples.Length)
                        Array.Resize(ref captureSamples, requiredSamples);
                    var sampleCount = ConvertToFloat(args.Buffer, args.BytesRecorded, waveFormat, captureSamples);
                    _analysis.PushInterleaved(captureSamples.AsSpan(0, sampleCount), waveFormat.Channels, waveFormat.SampleRate);
                }
                catch (Exception exception)
                {
                    _lastOutputError = exception.Message;
                }
            };
            capture.RecordingStopped += (_, args) =>
            {
                if (_disposed || !IsHandleCreated || IsDisposed) return;
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (_outputCapture != capture || _disposed) return;
                        _lastOutputError = args.Exception?.Message ?? "The default output stopped.";
                        StopDefaultOutput();
                        SetStatus($"RECONNECTING TO DEFAULT OUTPUT: {_lastOutputError}");
                    }));
                }
                catch (InvalidOperationException) { }
            };
            capture.StartRecording();
            _outputCapture = capture;
            _outputDeviceId = _outputDevice.ID;
            _lastOutputError = null;
            SetStatus($"LIVE · {_outputDevice.FriendlyName} · {waveFormat.SampleRate / 1000.0:0.#} kHz · {waveFormat.Channels} CH", true);
        }
        catch (Exception exception)
        {
            capture?.Dispose();
            _outputCapture = null;
            _outputDeviceId = null;
            _outputDevice?.Dispose();
            _outputDevice = null;
            _deviceEnumerator?.Dispose();
            _deviceEnumerator = null;
            _lastOutputError = exception.Message;
            SetStatus($"DEFAULT OUTPUT UNAVAILABLE: {exception.Message}");
        }
    }

    private static int ConvertToFloat(byte[] buffer, int bytesRecorded, WaveFormat format, float[] samples)
    {
        var bytesPerSample = format.BitsPerSample / 8;
        if (bytesPerSample is < 1 or > 4 || format.Channels < 1)
            throw new NotSupportedException($"Unsupported output format: {format.Encoding}, {format.BitsPerSample}-bit.");
        var sampleCount = bytesRecorded / bytesPerSample;
        if (sampleCount > samples.Length)
            throw new ArgumentException("The capture conversion buffer is too small.", nameof(samples));
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat;
        if (format.Encoding == WaveFormatEncoding.Extensible && format is WaveFormatExtensible extensible)
        {
            if (extensible.SubFormat != new Guid("00000003-0000-0010-8000-00aa00389b71") &&
                extensible.SubFormat != new Guid("00000001-0000-0010-8000-00aa00389b71"))
                throw new NotSupportedException($"Unsupported default output format: {extensible.SubFormat}.");
            isFloat = extensible.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
        }

        for (var i = 0; i < sampleCount; i++)
        {
            var offset = i * bytesPerSample;
            samples[i] = isFloat
                ? format.BitsPerSample == 32
                    ? BitConverter.ToSingle(buffer, offset)
                    : throw new NotSupportedException($"Unsupported floating-point output depth: {format.BitsPerSample}-bit.")
                : format.BitsPerSample switch
                {
                    8 => (buffer[offset] - 128) / 128f,
                    16 => BitConverter.ToInt16(buffer, offset) / 32768f,
                    24 => ReadInt24(buffer, offset) / 8388608f,
                    32 => BitConverter.ToInt32(buffer, offset) / 2147483648f,
                    _ => throw new NotSupportedException($"Unsupported PCM input depth: {format.BitsPerSample}-bit."),
                };
        }
        return sampleCount;
    }

    private static int ReadInt24(byte[] buffer, int offset)
    {
        var value = buffer[offset] | buffer[offset + 1] << 8 | buffer[offset + 2] << 16;
        return (value & 0x800000) != 0 ? value | unchecked((int)0xFF000000) : value;
    }

    private void StopDefaultOutput()
    {
        var capture = _outputCapture;
        _outputCapture = null;
        _outputDeviceId = null;
        if (capture is not null)
        {
            try { capture.StopRecording(); }
            catch (Exception exception) { _lastOutputError = exception.Message; }
            capture.Dispose();
        }
        _outputDevice?.Dispose();
        _outputDevice = null;
        _deviceEnumerator?.Dispose();
        _deviceEnumerator = null;
    }

    private void RefreshMeters()
    {
        if (_disposed) return;
        if (_lastOutputError is not null)
            SetStatus($"OUTPUT ERROR: {_lastOutputError}");
        var snapshot = _analysis.GetSnapshot();
        _snapshot = snapshot;
        if (!snapshot.HasSignal && _targetFps == 0)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastIdleRefresh).TotalMilliseconds < 33) return;
            _lastIdleRefresh = now;
        }
        if (snapshot.HasSignal && _lastDisplayedGeneration == snapshot.Generation) return;
        _lastDisplayedGeneration = snapshot.Generation;
        foreach (var chart in _charts) chart.SetSnapshot(snapshot);
    }

    private void SetStatus(string text, bool active = false)
    {
        _status.Text = active
            ? "● LIVE"
            : text.StartsWith("DEFAULT OUTPUT UNAVAILABLE", StringComparison.OrdinalIgnoreCase)
                ? "● NO OUTPUT"
                : text.StartsWith("RECONNECTING", StringComparison.OrdinalIgnoreCase)
                    ? "● RECONNECTING"
                    : text.StartsWith("OUTPUT ERROR", StringComparison.OrdinalIgnoreCase)
                        ? "● ERROR"
                        : "● OFFLINE";
        _status.ForeColor = active ? Palette.Accent : text.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || text.Contains("UNAVAILABLE", StringComparison.OrdinalIgnoreCase) ? Palette.Highlight : Muted;
        _statusTooltip.SetToolTip(_status, text);
    }

    private void ShowError(string message)
    {
        if (!IsDisposed) MessageBox.Show(this, message, "SPECTRM", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void DisposeAudio()
    {
        _disposed = true;
        _timer.Stop();
        _deviceTimer.Stop();
        _topBarTimer.Stop();
        _glideTimer.Stop();
        StopDefaultOutput();
        if (_timerResolutionActive)
        {
            timeEndPeriod(1);
            _timerResolutionActive = false;
        }
        _statusTooltip.Dispose();
        _timer.Dispose();
        _deviceTimer.Dispose();
        _topBarTimer.Dispose();
        _glideTimer.Dispose();
    }
}

internal sealed class SettingsOverlay : Control
{
    /// <summary>Smallest client area for the wide (strip) arrangement.</summary>
    public static readonly Size WideMinimum = new(1140, 120);

    // Every tile uses the same template: title top-left, description under it, key hint in the bottom-right corner.
    private const int TileWidth = 116;
    private const int TileHeight = 50;
    private const int LabelHeight = 16;
    private const int TilePadding = 13;
    /// <summary>Smallest client area for the tall (column) arrangement.</summary>
    public static readonly Size TallMinimum = new(300, 420);

    private enum ItemKind { Fps, Layout, Pin, Close }
    private sealed record Item(ItemKind Kind, int Value, string Key);

    private const int TileGap = 6;
    private const int GroupGap = 22;

    private static readonly Color Ink = Palette.Ink;
    private static readonly Color Muted = Palette.Label;
    private static readonly Color TileFill = Color.FromArgb(7, 15, 14);
    private static readonly Color TileHover = Palette.Hover;
    private static readonly Color TileSelected = Color.FromArgb(11, 28, 26);
    private static readonly Color TileBorder = Palette.Line;
    private static readonly Font EyebrowFont = new(UiFonts.Small, 7.5F, FontStyle.Bold);
    private static readonly Font HeadingFont = new(UiFonts.Display, 15F, FontStyle.Bold);
    private static readonly Font HintFont = new(UiFonts.Small, 7.5F, FontStyle.Bold);
    private static readonly Font GroupFont = new(UiFonts.Small, 7F, FontStyle.Bold);
    private static readonly Font TileTitleFont = new(UiFonts.Numbers, 9.5F);
    private static readonly Font TileDetailFont = new(UiFonts.Small, 7.5F, FontStyle.Bold);
    private static readonly Font TagFont = new(UiFonts.Numbers, 6.5F);
    private static readonly Font GlyphFont = new("Segoe MDL2 Assets", 7.5F);

    private static readonly Item[] FpsItems =
    {
        new(ItemKind.Fps, 30, "1"), new(ItemKind.Fps, 60, "2"), new(ItemKind.Fps, 120, "3"), new(ItemKind.Fps, 0, "4"),
    };
    private static readonly Item[] LayoutItems = { new(ItemKind.Layout, 0, "H"), new(ItemKind.Layout, 1, "V") };
    private static readonly Item PinItem = new(ItemKind.Pin, 0, "P");
    private static readonly Item CloseItem = new(ItemKind.Close, 0, "");

    private readonly List<(Rectangle Bounds, Item Item)> _items = new();
    private readonly List<(PointF Position, string Text)> _groupLabels = new();
    private Point _headingOrigin;
    private bool _wide = true;
    private int _hover = -1;
    private int _currentFps = 60;
    private bool _vertical;
    private bool _pinned;

    public event Action<int>? FpsChosen;
    public event Action<bool>? LayoutChosen;
    public event Action? PinToggled;
    public event Action? CloseRequested;

    public SettingsOverlay()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        BackColor = Color.Black;
        Visible = false;
        TabStop = true;
        AccessibleName = "SPECTRM options";
        AccessibleRole = AccessibleRole.Dialog;
    }

    public int CurrentFps
    {
        get => _currentFps;
        set { _currentFps = value; Invalidate(); }
    }

    public bool Vertical
    {
        get => _vertical;
        set { _vertical = value; Invalidate(); }
    }

    public bool Pinned
    {
        get => _pinned;
        set { _pinned = value; Invalidate(); }
    }

    public bool IsInteractive(Point point) => HitTest(point) >= 0;

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Arrange();
    }

    private void Arrange()
    {
        _items.Clear();
        _groupLabels.Clear();
        var width = ClientSize.Width;
        var height = ClientSize.Height;
        _wide = width >= height * 2 && width >= WideMinimum.Width - 40;
        if (_wide) ArrangeWide(width, height);
        else ArrangeTall(width, height);
        _items.Add((new Rectangle(width - 44, 0, 44, 30), CloseItem));
        Invalidate();
    }

    private void ArrangeWide(int width, int height)
    {
        // One block, vertically centred: group labels share a top line with the "SPECTRM / OPTIONS" eyebrow,
        // and every tile starts on the same line below them.
        const int textWidth = 196;
        var total = textWidth + GroupGap + (4 * TileWidth + 3 * TileGap) + GroupGap + (2 * TileWidth + TileGap) + GroupGap + TileWidth;
        var left = Math.Max(16, (width - total) / 2);
        var blockTop = (height - (LabelHeight + TileHeight)) / 2;
        _headingOrigin = new Point(left, blockTop);
        var top = blockTop + LabelHeight;
        var x = left + textWidth + GroupGap;

        void Group(string label, IEnumerable<Item> items)
        {
            _groupLabels.Add((new PointF(x, blockTop), label));
            foreach (var item in items)
            {
                _items.Add((new Rectangle(x, top, TileWidth, TileHeight), item));
                x += TileWidth + TileGap;
            }
            x += GroupGap - TileGap;
        }

        Group("FRAME RATE", FpsItems);
        Group("LAYOUT", LayoutItems);
        Group("WINDOW", new[] { PinItem });
    }

    private void ArrangeTall(int width, int height)
    {
        const int tileHeight = TileHeight, labelHeight = LabelHeight, sectionGap = 16, headingHeight = 84;
        var contentWidth = Math.Min(width - 32, 320);
        var left = (width - contentWidth) / 2;
        var half = (contentWidth - TileGap) / 2;
        var blockHeight = headingHeight
            + labelHeight + tileHeight * 2 + TileGap + sectionGap
            + labelHeight + tileHeight + sectionGap
            + labelHeight + tileHeight;
        var y = Math.Max(36, (height - blockHeight) / 2);
        _headingOrigin = new Point(left, y);
        y += headingHeight;

        _groupLabels.Add((new PointF(left, y), "FRAME RATE"));
        y += labelHeight;
        for (var i = 0; i < FpsItems.Length; i++)
            _items.Add((new Rectangle(left + (i % 2) * (half + TileGap), y + (i / 2) * (tileHeight + TileGap), half, tileHeight), FpsItems[i]));
        y += tileHeight * 2 + TileGap + sectionGap;

        _groupLabels.Add((new PointF(left, y), "LAYOUT"));
        y += labelHeight;
        for (var i = 0; i < LayoutItems.Length; i++)
            _items.Add((new Rectangle(left + i * (half + TileGap), y, half, tileHeight), LayoutItems[i]));
        y += tileHeight + sectionGap;

        _groupLabels.Add((new PointF(left, y), "WINDOW"));
        y += labelHeight;
        _items.Add((new Rectangle(left, y, contentWidth, tileHeight), PinItem));
    }

    private int HitTest(Point point)
    {
        for (var i = 0; i < _items.Count; i++)
            if (_items[i].Bounds.Contains(point)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hover = HitTest(e.Location);
        Cursor = hover >= 0 ? Cursors.Hand : Cursors.Default;
        if (hover == _hover) return;
        _hover = hover;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover == -1) return;
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        var hit = HitTest(e.Location);
        if (hit >= 0) Activate(_items[hit].Item);
    }

    private void Activate(Item item)
    {
        switch (item.Kind)
        {
            case ItemKind.Fps: FpsChosen?.Invoke(item.Value); break;
            case ItemKind.Layout: LayoutChosen?.Invoke(item.Value == 1); break;
            case ItemKind.Pin: PinToggled?.Invoke(); break;
            case ItemKind.Close: CloseRequested?.Invoke(); break;
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        Item? item = keyData switch
        {
            Keys.D1 or Keys.NumPad1 => FpsItems[0],
            Keys.D2 or Keys.NumPad2 => FpsItems[1],
            Keys.D3 or Keys.NumPad3 => FpsItems[2],
            Keys.D4 or Keys.NumPad4 => FpsItems[3],
            Keys.H => LayoutItems[0],
            Keys.V => LayoutItems[1],
            Keys.P => PinItem,
            _ => null,
        };
        if (item is not null)
        {
            Activate(item);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private bool IsSelected(Item item) => item.Kind switch
    {
        ItemKind.Fps => item.Value == _currentFps,
        ItemKind.Layout => (item.Value == 1) == _vertical,
        ItemKind.Pin => _pinned,
        _ => false,
    };

    private (string Title, string Detail) Describe(Item item) => item.Kind switch
    {
        ItemKind.Fps => item.Value switch
        {
            30 => ("30 FPS", "Lightweight"),
            60 => ("60 FPS", "Recommended"),
            120 => ("120 FPS", "Extra smooth"),
            _ => ("UNLIMITED", "No frame cap"),
        },
        ItemKind.Layout => item.Value == 1 ? ("VERTICAL", "Tall column") : ("HORIZONTAL", "Wide strip"),
        ItemKind.Pin => ("ON TOP", _pinned ? "Pinned" : "Off"),
        _ => ("", ""),
    };

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.Clear(BackColor);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = UiFonts.Rendering;

        using var inkBrush = new SolidBrush(Ink);
        using var accentBrush = new SolidBrush(Palette.Accent);
        using var mutedBrush = new SolidBrush(Muted);
        using var highlightBrush = new SolidBrush(Palette.Highlight);

        // Heading block
        var x = _headingOrigin.X;
        var y = _headingOrigin.Y;
        // The eyebrow sits on the same line as the group labels; heading and hints fill the tile height below it.
        graphics.DrawString("SPECTRM  /  OPTIONS", GroupFont, accentBrush, x, y);
        graphics.DrawString("Options", HeadingFont, inkBrush, x - 2, y + LabelHeight - 4);
        graphics.DrawString("1–4 rate  ·  H / V layout", HintFont, mutedBrush, x, y + LabelHeight + 24);
        graphics.DrawString("P pin  ·  Esc close", HintFont, mutedBrush, x, y + LabelHeight + 36);

        foreach (var (position, text) in _groupLabels)
            graphics.DrawString(text, GroupFont, mutedBrush, position.X, position.Y);

        for (var i = 0; i < _items.Count; i++)
        {
            var (tile, item) = _items[i];
            if (item.Kind == ItemKind.Close) continue;
            var selected = IsSelected(item);
            var hovered = i == _hover;
            using var path = RoundedRectangle(tile, 5);
            using (var fill = new SolidBrush(hovered ? TileHover : selected ? TileSelected : TileFill))
                graphics.FillPath(fill, path);
            using (var border = new Pen(selected ? Palette.Accent : hovered ? Color.FromArgb(48, 92, 84) : TileBorder))
                graphics.DrawPath(border, path);
            if (selected)
                graphics.FillRectangle(highlightBrush, tile.Left + 1, tile.Top + 11, 3, tile.Height - 22);

            var (title, detail) = Describe(item);
            graphics.DrawString(title, TileTitleFont, selected ? accentBrush : inkBrush, tile.Left + TilePadding, tile.Top + 8);
            graphics.DrawString(detail, TileDetailFont, mutedBrush, tile.Left + TilePadding, tile.Top + 27);

            // Key hint: a small keycap pinned to the bottom-right corner, level with the description.
            var keySize = graphics.MeasureString(item.Key, TagFont);
            var cap = new RectangleF(tile.Right - 8 - Math.Max(13, keySize.Width + 4), tile.Bottom - 8 - 13, Math.Max(13, keySize.Width + 4), 13);
            using (var capPath = RoundedRectangle(Rectangle.Round(cap), 3))
            using (var capPen = new Pen(selected ? Color.FromArgb(48, 92, 84) : TileBorder))
                graphics.DrawPath(capPen, capPath);
            graphics.DrawString(item.Key, TagFont, mutedBrush, cap.Left + (cap.Width - keySize.Width) / 2, cap.Top + (cap.Height - keySize.Height) / 2);
        }

        // Close button, same as the title bar
        var closeIndex = _items.FindIndex(entry => entry.Item.Kind == ItemKind.Close);
        if (closeIndex >= 0)
        {
            var closeBox = _items[closeIndex].Bounds;
            if (_hover == closeIndex)
            {
                using var red = new SolidBrush(Color.FromArgb(196, 43, 28));
                graphics.FillRectangle(red, closeBox);
            }
            var glyph = "";
            var glyphSize = graphics.MeasureString(glyph, GlyphFont);
            graphics.DrawString(glyph, GlyphFont, inkBrush,
                closeBox.Left + (closeBox.Width - glyphSize.Width) / 2,
                closeBox.Top + (closeBox.Height - glyphSize.Height) / 2);
        }
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        bounds.Width -= 1;
        bounds.Height -= 1;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class AnalysisSnapshot
{
    public bool HasSignal { get; set; }
    public long Generation { get; set; }
    public float[] Left { get; set; } = Array.Empty<float>();
    public float[] Right { get; set; } = Array.Empty<float>();
    public float[] SpectrumDb { get; set; } = Array.Empty<float>();
    public double ShortTermDb { get; set; } = -100;
    public double MomentaryLufs { get; set; } = -100;
    public double ShortTermLufs { get; set; } = -100;
    public double LufsLeft { get; set; } = -100;
    public double LufsRight { get; set; } = -100;
    public double RmsLeftDb { get; set; } = -100;
    public double RmsRightDb { get; set; } = -100;
    public int SampleRate { get; set; }
    public double Correlation { get; set; }
}

internal sealed class AnalysisEngine
{
    private readonly record struct BiquadCoefficients(double B0, double B1, double B2, double A1, double A2);

    private const int RingSize = 16384;
    public const int FftSize = 8192;
    private const double LevelSmoothingSeconds = 0.4;
    private readonly object _gate = new();
    private readonly float[] _left = new float[RingSize];
    private readonly float[] _right = new float[RingSize];
    private readonly float[] _analysisLeft = new float[FftSize];
    private readonly float[] _analysisRight = new float[FftSize];
    private readonly float[] _combined = new float[FftSize];
    private readonly double[] _fftReal = new double[FftSize];
    private readonly double[] _fftImaginary = new double[FftSize];
    private readonly double[,] _lufsFilterState = new double[2, 4];
    private readonly AnalysisSnapshot _snapshot = new()
    {
        Left = new float[FftSize],
        Right = new float[FftSize],
        SpectrumDb = new float[FftSize / 2],
    };
    private static readonly double[] HannWindow = BuildHannWindow();
    private static readonly int[] BitReversal = BuildBitReversal();
    private static readonly double[] TwiddleReal = BuildTwiddleReal();
    private static readonly double[] TwiddleImaginary = BuildTwiddleImaginary();
    private int _writePosition;
    private long _totalFrames;
    private long _generation;
    private long _snapshotGeneration = -1;
    private double _shortTermEnergy;
    private double _lufsEnergy;
    private double _lufsEnergyLeft;
    private double _lufsEnergyRight;
    private double _rmsEnergyLeft;
    private double _rmsEnergyRight;
    // EBU R128 short-term loudness: mean K-weighted energy over a sliding 3 s window, kept as 30 blocks of 100 ms.
    private const int ShortTermBlocks = 30;
    private readonly double[] _shortTermBlockEnergy = new double[ShortTermBlocks];
    private int _shortTermBlockCount;
    private int _shortTermBlockWrite;
    private double _shortTermAccumulator;
    private int _shortTermAccumulatedFrames;
    private int _lufsFilterSampleRate;
    private BiquadCoefficients _lufsHighShelf;
    private BiquadCoefficients _lufsHighPass;
    public void PushInterleaved(ReadOnlySpan<float> samples, int channels, int sampleRate)
    {
        if (channels < 1) return;
        lock (_gate)
        {
            if (_lufsFilterSampleRate != sampleRate)
                ConfigureLufsFilters(sampleRate);
            if (samples.Length == 0) return;
            var frames = samples.Length / channels;
            if (frames == 0) return;
            var blockEnergy = 0.0;
            var lufsBlockEnergy = 0.0;
            double lufsLeft = 0, lufsRight = 0, rmsLeft = 0, rmsRight = 0;
            for (var frame = 0; frame < frames; frame++)
            {
                var left = samples[frame * channels];
                var right = channels > 1 ? samples[frame * channels + 1] : left;
                var weightedLeft = FilterLufsSample(left, 0);
                var weightedRight = channels > 1 ? FilterLufsSample(right, 1) : 0;
                var weightedEnergy = weightedLeft * weightedLeft + weightedRight * weightedRight;
                lufsBlockEnergy += weightedEnergy;
                _shortTermAccumulator += weightedEnergy;
                if (++_shortTermAccumulatedFrames >= sampleRate / 10)
                {
                    _shortTermBlockEnergy[_shortTermBlockWrite] = _shortTermAccumulator / _shortTermAccumulatedFrames;
                    _shortTermBlockWrite = (_shortTermBlockWrite + 1) % ShortTermBlocks;
                    _shortTermBlockCount = Math.Min(_shortTermBlockCount + 1, ShortTermBlocks);
                    _shortTermAccumulator = 0;
                    _shortTermAccumulatedFrames = 0;
                }
                lufsLeft += weightedLeft * weightedLeft;
                lufsRight += channels > 1 ? weightedRight * weightedRight : weightedLeft * weightedLeft;
                rmsLeft += left * left;
                rmsRight += right * right;
                _left[_writePosition] = left;
                _right[_writePosition] = right;
                _writePosition = (_writePosition + 1) % RingSize;
                blockEnergy += (left * left + right * right) * 0.5;
                _totalFrames++;
            }
            var smoothing = Math.Exp(-(double)frames / (sampleRate * LevelSmoothingSeconds));
            _shortTermEnergy = smoothing * _shortTermEnergy + (1 - smoothing) * blockEnergy / frames;
            _lufsEnergy = smoothing * _lufsEnergy + (1 - smoothing) * lufsBlockEnergy / frames;
            _lufsEnergyLeft = smoothing * _lufsEnergyLeft + (1 - smoothing) * lufsLeft / frames;
            _lufsEnergyRight = smoothing * _lufsEnergyRight + (1 - smoothing) * lufsRight / frames;
            _rmsEnergyLeft = smoothing * _rmsEnergyLeft + (1 - smoothing) * rmsLeft / frames;
            _rmsEnergyRight = smoothing * _rmsEnergyRight + (1 - smoothing) * rmsRight / frames;
            _generation++;
        }
    }

    public AnalysisSnapshot GetSnapshot()
    {
        long generation;
        double cross = 0;
        double leftEnergy = 0;
        double rightEnergy = 0;
        double shortTermEnergy;
        double lufsEnergy;
        double lufsEnergyLeft, lufsEnergyRight, rmsEnergyLeft, rmsEnergyRight, shortTermLufsEnergy;
        int sampleRate;
        lock (_gate)
        {
            if (_totalFrames == 0)
            {
                _snapshot.HasSignal = false;
                return _snapshot;
            }
            if (_snapshotGeneration == _generation) return _snapshot;
            _snapshotGeneration = _generation;
            generation = _generation;
            shortTermEnergy = _shortTermEnergy;
            lufsEnergy = _lufsEnergy;
            lufsEnergyLeft = _lufsEnergyLeft;
            lufsEnergyRight = _lufsEnergyRight;
            rmsEnergyLeft = _rmsEnergyLeft;
            rmsEnergyRight = _rmsEnergyRight;
            sampleRate = _lufsFilterSampleRate;
            shortTermLufsEnergy = 0;
            for (var i = 0; i < _shortTermBlockCount; i++)
                shortTermLufsEnergy += _shortTermBlockEnergy[i];
            if (_shortTermBlockCount > 0) shortTermLufsEnergy /= _shortTermBlockCount;
            var available = (int)Math.Min(FftSize, _totalFrames);
            var start = (_writePosition - available + RingSize) % RingSize;
            Array.Clear(_analysisLeft);
            Array.Clear(_analysisRight);
            for (var i = 0; i < available; i++)
            {
                _analysisLeft[FftSize - available + i] = _left[(start + i) % RingSize];
                _analysisRight[FftSize - available + i] = _right[(start + i) % RingSize];
            }
        }

        var left = _snapshot.Left;
        var right = _snapshot.Right;
        Array.Copy(_analysisLeft, left, FftSize);
        Array.Copy(_analysisRight, right, FftSize);
        for (var i = 0; i < FftSize; i++)
        {
            var leftSample = left[i];
            var rightSample = right[i];
            cross += leftSample * rightSample;
            leftEnergy += leftSample * leftSample;
            rightEnergy += rightSample * rightSample;
            _combined[i] = (leftSample + rightSample) * 0.5f;
        }
        var shortDb = ToDb(Math.Sqrt(shortTermEnergy));
        CalculateSpectrum(_combined, _fftReal, _fftImaginary, _snapshot.SpectrumDb);
        _snapshot.HasSignal = true;
        _snapshot.Generation = generation;
        _snapshot.ShortTermDb = shortDb;
        _snapshot.MomentaryLufs = lufsEnergy > 1e-10 ? -0.691 + 10 * Math.Log10(lufsEnergy) : -100;
        _snapshot.LufsLeft = lufsEnergyLeft > 1e-10 ? -0.691 + 10 * Math.Log10(lufsEnergyLeft) : -100;
        _snapshot.LufsRight = lufsEnergyRight > 1e-10 ? -0.691 + 10 * Math.Log10(lufsEnergyRight) : -100;
        _snapshot.RmsLeftDb = ToDb(Math.Sqrt(rmsEnergyLeft));
        _snapshot.RmsRightDb = ToDb(Math.Sqrt(rmsEnergyRight));
        _snapshot.SampleRate = sampleRate;
        _snapshot.ShortTermLufs = shortTermLufsEnergy > 1e-10 ? -0.691 + 10 * Math.Log10(shortTermLufsEnergy) : -100;
        _snapshot.Correlation = leftEnergy * rightEnergy > 1e-14 ? Math.Clamp(cross / Math.Sqrt(leftEnergy * rightEnergy), -1, 1) : 0;
        return _snapshot;
    }

    public void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_left);
            Array.Clear(_right);
            _writePosition = 0;
            _totalFrames = 0;
            _generation++;
            _snapshotGeneration = -1;
            _snapshot.HasSignal = false;
            _shortTermEnergy = 0;
            _lufsEnergy = 0;
            _lufsEnergyLeft = 0;
            _lufsEnergyRight = 0;
            _rmsEnergyLeft = 0;
            _rmsEnergyRight = 0;
            ClearShortTerm();
            Array.Clear(_lufsFilterState);
        }
    }

    private void ClearShortTerm()
    {
        Array.Clear(_shortTermBlockEnergy);
        _shortTermBlockCount = 0;
        _shortTermBlockWrite = 0;
        _shortTermAccumulator = 0;
        _shortTermAccumulatedFrames = 0;
    }

    private void ConfigureLufsFilters(int sampleRate)
    {
        _lufsHighShelf = BuildHighShelf(sampleRate, 1681.974450955533, 3.999843853973347, 0.7071752369554196);
        _lufsHighPass = BuildHighPass(sampleRate, 38.13547087602444, 0.5003270373238773);
        Array.Clear(_lufsFilterState);
        _lufsEnergy = 0;
        ClearShortTerm();
        _lufsFilterSampleRate = sampleRate;
    }

    private double FilterLufsSample(double sample, int channel)
    {
        var shelf = _lufsHighShelf;
        var shelfOutput = shelf.B0 * sample + _lufsFilterState[channel, 0];
        _lufsFilterState[channel, 0] = shelf.B1 * sample - shelf.A1 * shelfOutput + _lufsFilterState[channel, 1];
        _lufsFilterState[channel, 1] = shelf.B2 * sample - shelf.A2 * shelfOutput;

        var highPass = _lufsHighPass;
        var output = highPass.B0 * shelfOutput + _lufsFilterState[channel, 2];
        _lufsFilterState[channel, 2] = highPass.B1 * shelfOutput - highPass.A1 * output + _lufsFilterState[channel, 3];
        _lufsFilterState[channel, 3] = highPass.B2 * shelfOutput - highPass.A2 * output;
        return output;
    }

    private static BiquadCoefficients BuildHighShelf(int sampleRate, double frequency, double gainDb, double slope)
    {
        var amplitude = Math.Pow(10, gainDb / 40);
        var omega = 2 * Math.PI * frequency / sampleRate;
        var cosine = Math.Cos(omega);
        var sine = Math.Sin(omega);
        var alpha = sine / 2 * Math.Sqrt((amplitude + 1 / amplitude) * (1 / slope - 1) + 2);
        var beta = 2 * Math.Sqrt(amplitude) * alpha;
        var a0 = (amplitude + 1) - (amplitude - 1) * cosine + beta;
        return new BiquadCoefficients(
            amplitude * ((amplitude + 1) + (amplitude - 1) * cosine + beta) / a0,
            -2 * amplitude * ((amplitude - 1) + (amplitude + 1) * cosine) / a0,
            amplitude * ((amplitude + 1) + (amplitude - 1) * cosine - beta) / a0,
            2 * ((amplitude - 1) - (amplitude + 1) * cosine) / a0,
            ((amplitude + 1) - (amplitude - 1) * cosine - beta) / a0);
    }

    private static BiquadCoefficients BuildHighPass(int sampleRate, double frequency, double q)
    {
        var omega = 2 * Math.PI * frequency / sampleRate;
        var cosine = Math.Cos(omega);
        var alpha = Math.Sin(omega) / (2 * q);
        var a0 = 1 + alpha;
        return new BiquadCoefficients(
            (1 + cosine) / (2 * a0),
            -(1 + cosine) / a0,
            (1 + cosine) / (2 * a0),
            -2 * cosine / a0,
            (1 - alpha) / a0);
    }

    private static double ToDb(double value) => value > 1e-7 ? 20 * Math.Log10(value) : -100;

    private static void CalculateSpectrum(float[] samples, double[] real, double[] imaginary, float[] result)
    {
        Array.Clear(imaginary);
        for (var i = 0; i < FftSize; i++)
            real[i] = samples[BitReversal[i]] * HannWindow[BitReversal[i]];
        for (var size = 2; size <= FftSize; size <<= 1)
        {
            var twiddleStride = FftSize / size;
            for (var start = 0; start < FftSize; start += size)
            {
                for (var offset = 0; offset < size / 2; offset++)
                {
                    var even = start + offset;
                    var odd = even + size / 2;
                    var twiddleIndex = offset * twiddleStride;
                    var factorReal = TwiddleReal[twiddleIndex];
                    var factorImaginary = TwiddleImaginary[twiddleIndex];
                    var oddReal = factorReal * real[odd] - factorImaginary * imaginary[odd];
                    var oddImaginary = factorReal * imaginary[odd] + factorImaginary * real[odd];
                    real[odd] = real[even] - oddReal;
                    imaginary[odd] = imaginary[even] - oddImaginary;
                    real[even] += oddReal;
                    imaginary[even] += oddImaginary;
                }
            }
        }
        for (var i = 1; i < result.Length; i++)
        {
            var magnitude = Math.Sqrt(real[i] * real[i] + imaginary[i] * imaginary[i]) / (FftSize * 0.25);
            result[i] = (float)ToDb(magnitude);
        }
    }

    private static double[] BuildHannWindow()
    {
        var result = new double[FftSize];
        for (var i = 0; i < result.Length; i++)
            result[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1));
        return result;
    }

    private static int[] BuildBitReversal()
    {
        var result = new int[FftSize];
        var bits = (int)Math.Log2(FftSize);
        for (var i = 0; i < result.Length; i++)
        {
            var reversed = 0;
            for (var bit = 0; bit < bits; bit++)
                reversed = (reversed << 1) | ((i >> bit) & 1);
            result[i] = reversed;
        }
        return result;
    }

    private static double[] BuildTwiddleReal()
    {
        var result = new double[FftSize / 2];
        for (var i = 0; i < result.Length; i++)
            result[i] = Math.Cos(-2 * Math.PI * i / FftSize);
        return result;
    }

    private static double[] BuildTwiddleImaginary()
    {
        var result = new double[FftSize / 2];
        for (var i = 0; i < result.Length; i++)
            result[i] = Math.Sin(-2 * Math.PI * i / FftSize);
        return result;
    }

}

internal sealed class MeterChart : Control
{
    private const double MinFrequency = 20;
    private const double MaxFrequency = 20000;
    private const int WaveformHistory = 4096;
    private const double VuReferenceLufs = -9; // 0 VU = -9 LUFS short-term (loud modern masters)
    private static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    private static readonly Color Ink = Palette.Ink;
    private static readonly Pen GridPen = new(Palette.Grid, 1F);
    private static readonly Pen SpectrumPen = new(Palette.Accent, 1.2F);
    private static readonly Pen ScopePen = new(Palette.Secondary, 1F);
    private static readonly Pen HoverPen = new(Color.FromArgb(80, Palette.Accent), 1F);
    private static readonly Pen VuInkPen = new(Ink, 2.4F);
    private static readonly Pen VuRedPen = new(Palette.Over, 2.4F);
    private static readonly Pen VuBaselinePen = new(Ink, 2F);
    private static readonly Pen NeedlePen = new(Palette.Needle, 2.2F);
    private static readonly SolidBrush InkBrush = new(Ink);
    private static readonly SolidBrush LabelBrush = new(Palette.Label);
    private static readonly SolidBrush AccentBrush = new(Palette.Accent);
    private static readonly SolidBrush TrackBrush = new(Palette.Track);
    private static readonly SolidBrush BoxBrush = new(Palette.Box);
    private static readonly SolidBrush VectorBrush = new(Color.FromArgb(160, Palette.Accent));
    private static readonly SolidBrush VuRedBrush = new(Palette.Over);
    private static readonly Font SmallFont = new(UiFonts.Numbers, 7F);
    private static readonly Font ValueFont = new(UiFonts.Numbers, 8.5F);
    private static readonly int[] InfernoLut = BuildInfernoLut();

    private readonly ChartKind _kind;
    private AnalysisSnapshot _snapshot = new();
    private DateTime _lastInvalidation = DateTime.MinValue;
    private bool _hasHistory;

    private Bitmap? _spectrogramBitmap;
    private int[] _spectrogramPixels = Array.Empty<int>();
    private DateTime _lastSpectrogramRow = DateTime.MinValue;

    private readonly float[] _wavePeaks = new float[WaveformHistory];
    private readonly int[] _waveColors = new int[WaveformHistory];
    private int _waveWrite;
    private int _waveCount;
    private DateTime _lastWaveColumn = DateTime.MinValue;

    private float[] _spectrumDb = Array.Empty<float>();
    private PointF[] _linePoints = Array.Empty<PointF>();
    private bool _hasSmoothedSpectrum;
    private Point? _hover;
    private double _needleVu = -40;
    private DateTime _lastNeedleUpdate = DateTime.MinValue;

    public MeterChart(ChartKind kind)
    {
        _kind = kind;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Color.Black;
        if (kind == ChartKind.Spectrum) Cursor = Cursors.Cross;
    }

    public void SetSnapshot(AnalysisSnapshot snapshot)
    {
        _snapshot = snapshot;
        var now = DateTime.UtcNow;
        if (snapshot.HasSignal)
        {
            if (_kind == ChartKind.Spectrogram && (now - _lastSpectrogramRow).TotalMilliseconds >= 25)
            {
                AppendSpectrogramRow(snapshot);
                _lastSpectrogramRow = now;
            }
            else if (_kind == ChartKind.Waveform && (now - _lastWaveColumn).TotalMilliseconds >= 15)
            {
                AppendWaveColumn(snapshot);
                _lastWaveColumn = now;
            }
        }
        else if (_hasHistory)
        {
            _hasHistory = false;
            _hasSmoothedSpectrum = false;
            _waveCount = 0;
            if (_spectrogramPixels.Length > 0)
            {
                Array.Fill(_spectrogramPixels, unchecked((int)0xFF000000));
                FlushSpectrogram();
            }
        }

        var interval = _kind switch { ChartKind.Spectrogram or ChartKind.Vectorscope => 25, _ => 7 };
        if ((now - _lastInvalidation).TotalMilliseconds >= interval)
        {
            _lastInvalidation = now;
            Invalidate();
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (EdgePassThrough.Handle(this, ref m)) return;
        base.WndProc(ref m);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_kind != ChartKind.Spectrum) return;
        _hover = e.Location;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover is null) return;
        _hover = null;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.Clear(BackColor);
        var rect = ClientRectangle;
        if (rect.Width < 8 || rect.Height < 8) return;
        graphics.TextRenderingHint = UiFonts.Rendering;
        var snapshot = _snapshot;
        switch (_kind)
        {
            case ChartKind.Spectrogram: DrawSpectrogram(graphics, rect); break;
            case ChartKind.Waveform: DrawWaveform(graphics, rect); break;
            case ChartKind.LufsMeter: DrawLufsMeter(graphics, rect, snapshot); break;
            case ChartKind.Vectorscope: DrawVectorscope(graphics, rect, snapshot); break;
            case ChartKind.VuMeter: DrawVuMeter(graphics, rect, snapshot); break;
            case ChartKind.Oscilloscope: DrawOscilloscope(graphics, rect, snapshot); break;
            case ChartKind.Spectrum: DrawSpectrum(graphics, rect, snapshot); break;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _spectrogramBitmap?.Dispose();
        base.Dispose(disposing);
    }

    // ---------- Spectrogram (log-frequency waterfall, newest row at the bottom) ----------

    private void AppendSpectrogramRow(AnalysisSnapshot snapshot)
    {
        var width = Math.Max(1, ClientSize.Width);
        var height = Math.Max(1, ClientSize.Height);
        if (_spectrogramBitmap is null || _spectrogramBitmap.Width != width || _spectrogramBitmap.Height != height)
        {
            _spectrogramBitmap?.Dispose();
            _spectrogramBitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            _spectrogramPixels = new int[width * height];
            Array.Fill(_spectrogramPixels, unchecked((int)0xFF000000));
        }

        Array.Copy(_spectrogramPixels, width, _spectrogramPixels, 0, width * (height - 1));
        var row = width * (height - 1);
        var binWidth = BinWidth(snapshot);
        for (var x = 0; x < width; x++)
        {
            var db = SampleBand(snapshot.SpectrumDb, binWidth, XToFrequency(x / (double)width), XToFrequency((x + 1) / (double)width));
            var level = Math.Pow(Math.Clamp((db + 92) / 80, 0, 1), 1.8);
            _spectrogramPixels[row + x] = InfernoLut[(int)(level * 255)];
        }
        FlushSpectrogram();
        _hasHistory = true;
    }

    private void FlushSpectrogram()
    {
        if (_spectrogramBitmap is null) return;
        var bounds = new Rectangle(0, 0, _spectrogramBitmap.Width, _spectrogramBitmap.Height);
        var data = _spectrogramBitmap.LockBits(bounds, ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            if (data.Stride == bounds.Width * 4)
                Marshal.Copy(_spectrogramPixels, 0, data.Scan0, _spectrogramPixels.Length);
            else
                for (var y = 0; y < bounds.Height; y++)
                    Marshal.Copy(_spectrogramPixels, y * bounds.Width, data.Scan0 + y * data.Stride, bounds.Width);
        }
        finally
        {
            _spectrogramBitmap.UnlockBits(data);
        }
    }

    private void DrawSpectrogram(Graphics graphics, Rectangle rect)
    {
        if (_spectrogramBitmap is null || !_hasHistory) return;
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.DrawImage(_spectrogramBitmap, rect);
    }

    // ---------- Colored scrolling waveform (hue follows the spectral centre) ----------

    private void AppendWaveColumn(AnalysisSnapshot snapshot)
    {
        var count = Math.Min(1024, snapshot.Left.Length);
        var start = snapshot.Left.Length - count;
        var peak = 0f;
        for (var i = start; i < snapshot.Left.Length; i++)
            peak = Math.Max(peak, Math.Max(Math.Abs(snapshot.Left[i]), Math.Abs(snapshot.Right[i])));

        var binWidth = BinWidth(snapshot);
        double weighted = 0, total = 0;
        var first = Math.Max(1, (int)(30 / binWidth));
        var last = Math.Min(snapshot.SpectrumDb.Length - 1, (int)(16000 / binWidth));
        for (var bin = first; bin <= last; bin++)
        {
            var magnitude = Math.Pow(10, snapshot.SpectrumDb[bin] / 20);
            weighted += magnitude * Math.Log2(bin * binWidth);
            total += magnitude;
        }
        var centroid = total > 1e-9 ? weighted / total : Math.Log2(1000);
        var hue = Math.Clamp((centroid - Math.Log2(150)) / (Math.Log2(6000) - Math.Log2(150)), 0, 1) * 275;
        var saturation = Math.Clamp(0.9 - peak * 0.6, 0.15, 0.9);

        _wavePeaks[_waveWrite] = Math.Min(1f, peak);
        _waveColors[_waveWrite] = WaveColor(hue / 275, peak);
        _waveWrite = (_waveWrite + 1) % WaveformHistory;
        _waveCount = Math.Min(_waveCount + 1, WaveformHistory);
        _hasHistory = true;
    }

    private void DrawWaveform(Graphics graphics, Rectangle rect)
    {
        graphics.SmoothingMode = SmoothingMode.None;
        var centerY = rect.Top + rect.Height / 2f;
        var half = rect.Height * 0.32f;
        var columns = Math.Min(_waveCount, rect.Width);
        using var brush = new SolidBrush(Color.Black);
        for (var i = 0; i < columns; i++)
        {
            var index = (_waveWrite - 1 - i + WaveformHistory) % WaveformHistory;
            var amplitude = Math.Max(0.6f, _wavePeaks[index] * half);
            brush.Color = Color.FromArgb(_waveColors[index]);
            graphics.FillRectangle(brush, rect.Right - 1 - i, centerY - amplitude, 1, amplitude * 2);
        }
    }

    // ---------- LUFS bars ----------

    private static void DrawLufsMeter(Graphics graphics, Rectangle rect, AnalysisSnapshot snapshot)
    {
        graphics.SmoothingMode = SmoothingMode.None;
        const double range = 54;
        var top = rect.Top + 8f;
        var bottom = rect.Bottom - 6f;
        float Y(double db) => top + (float)(Math.Clamp(-db, 0, range) / range) * (bottom - top);

        // Centre the scale + bars + readout when the panel is wider than they need (vertical layout).
        var contentWidth = 22f + 4 + 8 * 2 + 3 + 4 + graphics.MeasureString("-88.8 LUFS", ValueFont).Width + 8;
        var labelRight = rect.Left + Math.Max(0, (rect.Width - contentWidth) / 2) + 22f;
        foreach (var mark in new[] { 0, 6, 12, 24, 36, 50 })
        {
            var text = mark.ToString();
            var size = graphics.MeasureString(text, SmallFont);
            graphics.DrawString(text, SmallFont, LabelBrush, labelRight - size.Width, Y(-mark) - size.Height / 2);
        }

        const float barWidth = 8f;
        const float gap = 3f;
        var barX = labelRight + 4;
        void Bar(float x, double level)
        {
            graphics.FillRectangle(TrackBrush, x, top, barWidth, bottom - top);
            if (!snapshot.HasSignal || level <= -range) return;
            var y = Y(level);
            graphics.FillRectangle(AccentBrush, x, y, barWidth, bottom - y);
        }
        Bar(barX, snapshot.LufsLeft);
        Bar(barX + barWidth + gap, snapshot.LufsRight);

        var lufs = snapshot.HasSignal ? snapshot.MomentaryLufs : -100;
        var readout = lufs > -100 ? $"{lufs:0.0} LUFS" : "-inf LUFS";
        var readoutSize = graphics.MeasureString(readout, ValueFont);
        var boxY = Math.Clamp(Y(lufs) - readoutSize.Height / 2, top, Math.Max(top, bottom - readoutSize.Height - 2));
        DrawValueBox(graphics, readout, ValueFont, barX + barWidth * 2 + gap + 4, boxY);
    }

    // ---------- Vectorscope ----------

    private static void DrawVectorscope(Graphics graphics, Rectangle rect, AnalysisSnapshot snapshot)
    {
        if (!snapshot.HasSignal) return;
        graphics.SmoothingMode = SmoothingMode.None;
        var centerX = rect.Left + rect.Width / 2f;
        var centerY = rect.Top + rect.Height / 2f;
        var scale = rect.Height * 0.42f;
        var count = Math.Min(2048, snapshot.Left.Length);
        var start = snapshot.Left.Length - count;
        for (var i = start; i < snapshot.Left.Length; i++)
        {
            var left = snapshot.Left[i];
            var right = snapshot.Right[i];
            var x = centerX + (left - right) * 0.7071f * scale;
            var y = centerY - (left + right) * 0.7071f * scale;
            if (x < rect.Left || x >= rect.Right || y < rect.Top || y >= rect.Bottom) continue;
            graphics.FillRectangle(VectorBrush, x, y, 1.4f, 1.4f);
        }
    }

    // ---------- Analog VU meter ----------

    private static double VuPosition(double vu) => Math.Pow(10, vu / 20) / 1.4125;

    private void DrawVuMeter(Graphics graphics, Rectangle rect, AnalysisSnapshot snapshot)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var h = (float)rect.Height;
        const double halfSpan = 24 * Math.PI / 180;
        var halfWidth = Math.Max(20f, Math.Min(rect.Width * 0.42f, h * 1.15f));
        var radius = (float)(halfWidth / Math.Sin(halfSpan));
        var centerX = rect.Left + rect.Width / 2f;
        var arcY = rect.Top + h * 0.38f;
        var pivotY = arcY + radius;

        PointF At(double position, float distance)
        {
            var angle = -halfSpan + position * 2 * halfSpan;
            return new PointF(centerX + (float)Math.Sin(angle) * distance, pivotY - (float)Math.Cos(angle) * distance);
        }
        float GdiAngle(double position) => (float)((-halfSpan + position * 2 * halfSpan) * 180 / Math.PI - 90);

        var zero = VuPosition(0);
        using (var band = new GraphicsPath())
        {
            var outer = radius + h * 0.035f;
            var inner = radius - h * 0.035f;
            var a0 = GdiAngle(zero);
            var a1 = GdiAngle(1);
            band.AddArc(centerX - outer, pivotY - outer, outer * 2, outer * 2, a0, a1 - a0);
            band.AddArc(centerX - inner, pivotY - inner, inner * 2, inner * 2, a1, a0 - a1);
            band.CloseFigure();
            graphics.FillPath(VuRedBrush, band);
        }
        var start = VuPosition(-20);
        graphics.DrawArc(VuBaselinePen, centerX - radius, pivotY - radius, radius * 2, radius * 2, GdiAngle(start), GdiAngle(zero) - GdiAngle(start));

        var fontPx = Math.Clamp(h * 0.12f, 9f, 20f);
        using var scaleFont = new Font(UiFonts.Numbers, fontPx, FontStyle.Regular, GraphicsUnit.Pixel);
        using var percentFont = new Font(UiFonts.Numbers, fontPx * 0.72f, FontStyle.Regular, GraphicsUnit.Pixel);
        foreach (var mark in new[] { -20, -10, -7, -5, -4, -3, -2, -1, 0, 1, 2, 3 })
        {
            var position = VuPosition(mark);
            var red = mark > 0;
            graphics.DrawLine(red ? VuRedPen : VuInkPen, At(position, radius), At(position, radius + h * 0.13f));
            var label = Math.Abs(mark).ToString();
            var point = At(position, radius + h * 0.23f);
            var size = graphics.MeasureString(label, scaleFont);
            graphics.DrawString(label, scaleFont, red ? VuRedBrush : InkBrush, point.X - size.Width / 2, point.Y - size.Height / 2);
        }

        for (var percent = 0; percent <= 100; percent += 20)
        {
            var position = percent / 100.0 * zero;
            var dot = At(position, radius - h * 0.11f);
            graphics.FillEllipse(InkBrush, dot.X - 2, dot.Y - 2, 4, 4);
            var label = percent.ToString();
            var point = At(position, radius - h * 0.21f);
            var size = graphics.MeasureString(label, percentFont);
            graphics.DrawString(label, percentFont, InkBrush, point.X - size.Width / 2, point.Y - size.Height / 2);
        }

        // Readouts: short-term loudness on the left, distance from the 0 VU reference on the right.
        var shortTerm = snapshot.HasSignal ? snapshot.ShortTermLufs : -100;
        var hasLoudness = shortTerm > -100;
        var leftText = hasLoudness ? $"{shortTerm:0.0} LUFS-S" : "-inf LUFS-S";
        var rightText = hasLoudness ? $"{shortTerm - VuReferenceLufs:+0.0;-0.0} LU" : "-- LU";
        var boxY = rect.Bottom - ValueFont.Height - 6f;
        DrawValueBox(graphics, leftText, ValueFont, rect.Left + 6, boxY);
        DrawValueBox(graphics, rightText, ValueFont, rect.Right - 6, boxY, true);

        // Needle ballistics: ease toward the target (~300 ms), like an analog movement.
        var target = hasLoudness ? shortTerm - VuReferenceLufs : -40;
        var now = DateTime.UtcNow;
        var elapsed = _lastNeedleUpdate == DateTime.MinValue ? 1 : Math.Min(1, (now - _lastNeedleUpdate).TotalSeconds);
        _lastNeedleUpdate = now;
        _needleVu += (target - _needleVu) * (1 - Math.Exp(-elapsed / 0.3));
        var needle = Math.Clamp(VuPosition(_needleVu), -0.02, 1.04);
        var state = graphics.Save();
        graphics.SetClip(rect);
        graphics.DrawLine(NeedlePen, At(needle, 0), At(needle, radius + h * 0.2f));
        graphics.Restore(state);
    }

    // ---------- Oscilloscope ----------

    private static void DrawOscilloscope(Graphics graphics, Rectangle rect, AnalysisSnapshot snapshot)
    {
        graphics.SmoothingMode = SmoothingMode.None;
        var centerY = rect.Top + rect.Height / 2f;
        var left = rect.Left + (int)(rect.Width * 0.04f);
        var columns = Math.Max(2, (int)(rect.Width * 0.92f));
        if (!snapshot.HasSignal)
        {
            graphics.DrawLine(ScopePen, left, centerY, left + columns, centerY);
            return;
        }
        var samples = Math.Min(2048, snapshot.Left.Length);
        var start = snapshot.Left.Length - samples;
        var amplitude = rect.Height * 0.3f;
        for (var x = 0; x < columns; x++)
        {
            var from = start + x * samples / columns;
            var to = Math.Max(from + 1, start + (x + 1) * samples / columns);
            var min = float.MaxValue;
            var max = float.MinValue;
            for (var i = from; i < to; i++)
            {
                var mono = (snapshot.Left[i] + snapshot.Right[i]) * 0.5f;
                min = Math.Min(min, mono);
                max = Math.Max(max, mono);
            }
            var y0 = centerY - Math.Clamp(max * amplitude, -rect.Height / 2f, rect.Height / 2f);
            var y1 = centerY - Math.Clamp(min * amplitude, -rect.Height / 2f, rect.Height / 2f);
            graphics.DrawLine(ScopePen, left + x, y0, left + x, Math.Max(y0 + 1, y1));
        }
    }

    // ---------- Spectrum analyser with hover readout ----------

    private void DrawSpectrum(Graphics graphics, Rectangle rect, AnalysisSnapshot snapshot)
    {
        graphics.SmoothingMode = SmoothingMode.None;
        for (var decade = 10; decade <= 10000; decade *= 10)
        {
            for (var multiple = 1; multiple <= 9; multiple++)
            {
                var frequency = decade * multiple;
                if (frequency < MinFrequency || frequency > MaxFrequency) continue;
                var x = rect.Left + (float)FrequencyToFraction(frequency) * (rect.Width - 1);
                graphics.DrawLine(GridPen, x, rect.Top, x, rect.Bottom);
                if (multiple == 1 && decade >= 100)
                {
                    var label = decade >= 1000 ? $"{decade / 1000}kHz" : $"{decade}Hz";
                    var labelWidth = graphics.MeasureString(label, SmallFont).Width;
                    // Keep the label inside the panel: flip it to the left of its line near the right edge.
                    var labelX = x + 2 + labelWidth > rect.Right ? x - 2 - labelWidth : x + 2;
                    graphics.DrawString(label, SmallFont, LabelBrush, labelX, rect.Top + 1);
                }
            }
        }
        graphics.DrawLine(GridPen, rect.Right - 1, rect.Top, rect.Right - 1, rect.Bottom);

        if (!snapshot.HasSignal) return;
        var width = rect.Width;
        if (_spectrumDb.Length != width)
        {
            _spectrumDb = new float[width];
            _linePoints = new PointF[width];
            _hasSmoothedSpectrum = false;
        }
        var binWidth = BinWidth(snapshot);
        for (var x = 0; x < width; x++)
        {
            var db = SampleBand(snapshot.SpectrumDb, binWidth, XToFrequency(x / (double)width), XToFrequency((x + 1) / (double)width));
            _spectrumDb[x] = _hasSmoothedSpectrum ? _spectrumDb[x] + (db - _spectrumDb[x]) * 0.45f : db;
        }
        _hasSmoothedSpectrum = true;
        for (var x = 0; x < width; x++)
        {
            var level = Math.Clamp((_spectrumDb[x] + 96) / 84f, 0, 1);
            _linePoints[x] = new PointF(rect.Left + x, rect.Bottom - 1 - level * (rect.Height - 2));
        }
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.DrawLines(SpectrumPen, _linePoints);

        if (_hover is { } hover && rect.Contains(hover))
        {
            var hx = Math.Clamp(hover.X - rect.Left, 0, width - 1);
            var frequency = XToFrequency((hx + 0.5) / width);
            var text = $"{_spectrumDb[hx]:0.00}dB | {frequency:0.00}Hz | {NoteName(frequency)}";
            graphics.SmoothingMode = SmoothingMode.None;
            graphics.DrawLine(HoverPen, hover.X, rect.Top, hover.X, rect.Bottom);
            var size = graphics.MeasureString(text, ValueFont);
            var boxX = hover.X + 10f;
            if (boxX + size.Width + 6 > rect.Right) boxX = hover.X - 10 - size.Width - 6;
            var boxY = Math.Clamp(hover.Y - size.Height / 2, rect.Top + 2, Math.Max(rect.Top + 2, rect.Bottom - size.Height - 4));
            DrawValueBox(graphics, text, ValueFont, Math.Max(rect.Left, boxX), boxY);
        }
    }

    // ---------- Helpers ----------

    private static void DrawValueBox(Graphics graphics, string text, Font font, float x, float y, bool alignRight = false)
    {
        var size = graphics.MeasureString(text, font);
        var width = size.Width + 8;
        var height = size.Height + 3;
        var left = alignRight ? x - width : x;
        var state = graphics.Save();
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var box = RoundedBox(new RectangleF(left, y, width, height), 3))
            graphics.FillPath(BoxBrush, box);
        graphics.Restore(state);
        graphics.DrawString(text, font, InkBrush, left + 4, y + 1.5f);
    }

    private static GraphicsPath RoundedBox(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static double BinWidth(AnalysisSnapshot snapshot) =>
        (snapshot.SampleRate > 0 ? snapshot.SampleRate : 48000) / (double)AnalysisEngine.FftSize;

    private static double XToFrequency(double fraction) => MinFrequency * Math.Pow(MaxFrequency / MinFrequency, fraction);

    private static double FrequencyToFraction(double frequency) => Math.Log(frequency / MinFrequency) / Math.Log(MaxFrequency / MinFrequency);

    private static float SampleBand(float[] spectrum, double binWidth, double lowFrequency, double highFrequency)
    {
        var lowBin = lowFrequency / binWidth;
        var highBin = highFrequency / binWidth;
        if (highBin - lowBin < 1)
        {
            var position = Math.Clamp((lowBin + highBin) / 2, 1, spectrum.Length - 1.001);
            var lower = (int)position;
            return spectrum[lower] + (spectrum[lower + 1] - spectrum[lower]) * (float)(position - lower);
        }
        var first = Math.Clamp((int)Math.Ceiling(lowBin), 1, spectrum.Length - 1);
        var last = Math.Clamp((int)Math.Floor(highBin), first, spectrum.Length - 1);
        var max = -200f;
        for (var bin = first; bin <= last; bin++)
            max = Math.Max(max, spectrum[bin]);
        return max;
    }

    private static string NoteName(double frequency)
    {
        var midi = 69 + 12 * Math.Log2(frequency / 440);
        var nearest = (int)Math.Round(midi);
        var cents = (int)Math.Round((midi - nearest) * 100);
        var name = NoteNames[((nearest % 12) + 12) % 12];
        var octave = (int)Math.Floor(nearest / 12.0) - 1;
        return $"{name}{octave} {(cents < 0 ? "-" : "+")} {Math.Abs(cents)} Cents";
    }

    /// <summary>Waveform colour: pink for bass-heavy sound, violet for mids, mint for bright/high content; loud peaks lift toward white.</summary>
    private static int WaveColor(double brightness, float peak)
    {
        var stops = new[] { Palette.Highlight, Palette.Secondary, Palette.Accent };
        var scaled = Math.Clamp(brightness, 0, 1) * (stops.Length - 1);
        var index = Math.Min(stops.Length - 2, (int)scaled);
        var amount = scaled - index;
        double Channel(int from, int to) => from + (to - from) * amount;
        var r = Channel(stops[index].R, stops[index + 1].R);
        var g = Channel(stops[index].G, stops[index + 1].G);
        var b = Channel(stops[index].B, stops[index + 1].B);
        var lift = Math.Clamp(peak - 0.55, 0, 0.45) / 0.45 * 0.5;
        return Color.FromArgb(
            (int)(r + (255 - r) * lift),
            (int)(g + (255 - g) * lift),
            (int)(b + (255 - b) * lift)).ToArgb();
    }

    private static int HsvToArgb(double hue, double saturation, double value)
    {
        var chroma = value * saturation;
        var sector = hue / 60;
        var x = chroma * (1 - Math.Abs(sector % 2 - 1));
        var m = value - chroma;
        var (r, g, b) = sector switch
        {
            < 1 => (chroma, x, 0d),
            < 2 => (x, chroma, 0d),
            < 3 => (0d, chroma, x),
            < 4 => (0d, x, chroma),
            < 5 => (x, 0d, chroma),
            _ => (chroma, 0d, x),
        };
        return Color.FromArgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255)).ToArgb();
    }

    private static int[] BuildInfernoLut()
    {
        var stops = new[]
        {
            // "Deep sea": black, navy, blue, teal, mint, white.
            (0, 0, 0), (4, 10, 26), (10, 22, 60), (20, 40, 104), (30, 70, 150), (28, 110, 170),
            (30, 155, 172), (60, 200, 180), (112, 240, 200), (190, 252, 228), (255, 255, 255),
        };
        var lut = new int[256];
        for (var i = 0; i < lut.Length; i++)
        {
            var scaled = i / 255.0 * (stops.Length - 1);
            var index = Math.Min(stops.Length - 2, (int)scaled);
            var amount = scaled - index;
            var (r0, g0, b0) = stops[index];
            var (r1, g1, b1) = stops[index + 1];
            lut[i] = Color.FromArgb(
                (int)(r0 + (r1 - r0) * amount),
                (int)(g0 + (g1 - g0) * amount),
                (int)(b0 + (b1 - b0) * amount)).ToArgb();
        }
        return lut;
    }
}
