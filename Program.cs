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

/// <summary>
/// One colour theme: three accent colours (primary, secondary, warning/"over"), tinted greys for text and
/// surfaces, and the spectrogram's colour ramp (quiet to loud). Backgrounds stay black in every theme.
/// </summary>
internal sealed class ColorTheme
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required Color Accent { get; init; }
    public required Color Secondary { get; init; }
    public required Color Highlight { get; init; }
    public required Color Ink { get; init; }
    public required Color Label { get; init; }
    public required Color Grid { get; init; }
    public required Color Track { get; init; }
    public required Color Box { get; init; }
    public required Color Line { get; init; }
    public required Color Hover { get; init; }
    public required Color[] Spectrogram { get; init; }

    private static Color C(int r, int g, int b) => Color.FromArgb(r, g, b);

    /// <summary>Theme whose greys are tinted by (r, g, b) multipliers in 0..1.</summary>
    private static ColorTheme Tinted(string id, string name, Color accent, Color secondary, Color highlight,
        double r, double g, double b, params Color[] spectrogram)
    {
        Color Grey(double level) => C((int)(level * r), (int)(level * g), (int)(level * b));
        Color InkTint() => C((int)(240 * (1 - (1 - r) * 0.2)), (int)(240 * (1 - (1 - g) * 0.2)), (int)(240 * (1 - (1 - b) * 0.2)));
        return new ColorTheme
        {
            Id = id, Name = name, Accent = accent, Secondary = secondary, Highlight = highlight,
            Ink = InkTint(), Label = Grey(138), Grid = Grey(33), Track = Grey(22), Box = Grey(38), Line = Grey(47), Hover = Grey(36),
            Spectrogram = spectrogram,
        };
    }

    public static readonly ColorTheme[] All =
    {
        new()
        {
            Id = "deepsea", Name = "Deep Sea",
            Accent = C(112, 240, 200), Secondary = C(150, 132, 255), Highlight = C(255, 84, 140),
            Ink = C(228, 240, 236), Label = C(108, 136, 132), Grid = C(17, 33, 31), Track = C(10, 22, 21),
            Box = C(15, 38, 36), Line = C(26, 46, 44), Hover = C(18, 36, 34),
            Spectrogram = new[]
            {
                C(0, 0, 0), C(4, 10, 26), C(10, 22, 60), C(20, 40, 104), C(30, 70, 150), C(28, 110, 170),
                C(30, 155, 172), C(60, 200, 180), C(112, 240, 200), C(190, 252, 228), C(255, 255, 255),
            },
        },
        Tinted("aurora", "Aurora", C(110, 245, 150), C(90, 200, 255), C(255, 96, 200), 0.80, 1.0, 0.86,
            C(0, 0, 0), C(4, 16, 10), C(8, 34, 22), C(12, 60, 40), C(20, 96, 60), C(40, 140, 80),
            C(70, 190, 110), C(120, 230, 140), C(170, 250, 190), C(220, 255, 235), C(255, 255, 255)),
        Tinted("synthwave", "Synthwave", C(255, 96, 214), C(96, 220, 255), C(255, 214, 90), 0.92, 0.78, 1.0,
            C(0, 0, 0), C(14, 4, 30), C(32, 8, 64), C(58, 14, 100), C(96, 20, 130), C(140, 30, 150),
            C(190, 50, 170), C(235, 80, 190), C(255, 130, 210), C(255, 200, 235), C(255, 255, 255)),
        Tinted("glacier", "Glacier", C(130, 205, 255), C(184, 168, 255), C(255, 96, 120), 0.80, 0.91, 1.0,
            C(0, 0, 0), C(2, 8, 24), C(6, 18, 52), C(12, 34, 90), C(22, 56, 130), C(40, 90, 170),
            C(70, 130, 205), C(110, 170, 235), C(160, 205, 250), C(210, 232, 255), C(255, 255, 255)),
        Tinted("graphite", "Graphite", C(236, 236, 236), C(150, 150, 160), C(255, 72, 72), 1.0, 1.0, 1.0,
            C(0, 0, 0), C(12, 12, 12), C(26, 26, 26), C(42, 42, 42), C(60, 60, 60), C(82, 82, 82),
            C(108, 108, 108), C(138, 138, 138), C(170, 170, 170), C(212, 212, 212), C(255, 255, 255)),
    };

    public static ColorTheme ById(string? id) =>
        All.FirstOrDefault(theme => string.Equals(theme.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];
}

/// <summary>
/// The active SPECTRM colours. Everything reads them at paint time; <see cref="Changed"/> fires when the user
/// picks another theme so cached pens, brushes and colour tables can be rebuilt.
/// </summary>
internal static class Palette
{
    public static ColorTheme Theme { get; private set; } = ColorTheme.All[0];
    public static event Action? Changed;

    public static Color Accent => Theme.Accent;
    public static Color Secondary => Theme.Secondary;
    public static Color Highlight => Theme.Highlight;
    public static Color Needle => Theme.Accent;
    public static Color Over => Theme.Highlight;
    public static Color Ink => Theme.Ink;
    public static Color Label => Theme.Label;
    public static Color Grid => Theme.Grid;
    public static Color Track => Theme.Track;
    public static Color Box => Theme.Box;
    public static Color Line => Theme.Line;
    public static Color Hover => Theme.Hover;

    public static void Apply(ColorTheme theme)
    {
        if (ReferenceEquals(theme, Theme)) return;
        Theme = theme;
        Changed?.Invoke();
    }

    public static Color Mix(Color a, Color b, double amount) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * amount), (int)(a.G + (b.G - a.G) * amount), (int)(a.B + (b.B - a.B) * amount));
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

internal enum BarGlyph { Layout, Pin, Options, Minimize, Maximize, Restore, Close }

/// <summary>
/// Top-bar button drawn entirely by SPECTRM: thin vector line icons in the palette's muted grey that brighten on
/// hover, a soft rounded highlight that fades in, and an accent colour when switched on (pin).
/// </summary>
internal sealed class BarButton : Button
{
    private readonly System.Windows.Forms.Timer _fade = new() { Interval = 15 };
    private float _hover;
    private bool _over;
    private bool _pressed;

    public BarButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        _fade.Tick += (_, _) =>
        {
            var target = _over ? 1f : 0f;
            _hover += Math.Sign(target - _hover) * 0.16f;
            if (Math.Abs(_hover - target) < 0.17f) { _hover = target; _fade.Stop(); }
            Invalidate();
        };
    }

    public BarGlyph Glyph { get; set; }

    /// <summary>Window controls fill the whole cell on hover; tool buttons get a rounded pill.</summary>
    public bool WindowControl { get; set; }

    /// <summary>On-state (pin): the icon is drawn in the accent colour.</summary>
    public bool Active { get => _active; set { _active = value; Invalidate(); } }
    private bool _active;

    /// <summary>For the layout icon: true when the window is currently vertical.</summary>
    public bool Vertical { get => _vertical; set { _vertical = value; Invalidate(); } }
    private bool _vertical;

    protected override void OnMouseEnter(EventArgs e) { _over = true; _fade.Start(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _over = false; _pressed = false; _fade.Start(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _fade.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.Black);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var danger = Glyph == BarGlyph.Close;
        if (_hover > 0)
        {
            var fill = danger ? Color.FromArgb(196, 43, 28) : _pressed ? Palette.Line : Palette.Hover;
            using var brush = new SolidBrush(Color.FromArgb((int)(255 * _hover), fill));
            if (WindowControl) g.FillRectangle(brush, ClientRectangle);
            else
            {
                using var pill = Rounded(new RectangleF(3, 4, Width - 6, Height - 8), 6);
                g.FillPath(brush, pill);
            }
        }
        if (Focused && ShowFocusCues)
        {
            using var ring = new Pen(Palette.Accent);
            using var pill = Rounded(new RectangleF(3.5f, 4.5f, Width - 7, Height - 9), 6);
            g.DrawPath(ring, pill);
        }

        var rest = _active ? Palette.Accent : Palette.Mix(Palette.Label, Palette.Ink, 0.3);
        var lit = _active ? Palette.Accent : danger ? Color.White : Palette.Ink;
        var color = Palette.Mix(rest, lit, _hover);
        using var pen = new Pen(color, 1.3f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var solid = new SolidBrush(color);
        var cx = Width / 2f;
        var cy = Height / 2f;
        switch (Glyph)
        {
            case BarGlyph.Layout:
            {
                // The arrangement it switches to: a panel split into three, side by side or stacked.
                var w = _vertical ? 14f : 9f;
                var h = _vertical ? 9f : 14f;
                using (var frame = Rounded(new RectangleF(cx - w / 2, cy - h / 2, w, h), 2)) g.DrawPath(pen, frame);
                for (var i = 1; i < 3; i++)
                {
                    if (_vertical) { var x = cx - w / 2 + w * i / 3; g.DrawLine(pen, x, cy - h / 2 + 1.5f, x, cy + h / 2 - 1.5f); }
                    else { var y = cy - h / 2 + h * i / 3; g.DrawLine(pen, cx - w / 2 + 1.5f, y, cx + w / 2 - 1.5f, y); }
                }
                break;
            }
            case BarGlyph.Pin:
            {
                // Always on top: a window sitting above another one; filled in when pinned.
                using (var back = Rounded(new RectangleF(cx - 6, cy - 2, 9, 8), 2)) g.DrawPath(pen, back);
                using var front = Rounded(new RectangleF(cx - 3, cy - 6, 9, 8), 2);
                using (var clear = new SolidBrush(Color.Black)) g.FillPath(clear, front);
                if (_hover > 0 && !WindowControl)
                {
                    using var under = new SolidBrush(Color.FromArgb((int)(255 * _hover), _pressed ? Palette.Line : Palette.Hover));
                    g.FillPath(under, front);
                }
                if (_active) g.FillPath(solid, front); else g.DrawPath(pen, front);
                break;
            }
            case BarGlyph.Options:
            {
                // Three faders, like a mixer strip.
                var xs = new[] { cx - 5, cx, cx + 5 };
                var knobs = new[] { cy + 2.5f, cy - 3f, cy + 0.5f };
                for (var i = 0; i < 3; i++)
                {
                    g.DrawLine(pen, xs[i], cy - 6.5f, xs[i], cy + 6.5f);
                    var knob = new RectangleF(xs[i] - 2.5f, knobs[i] - 1.5f, 5, 3);
                    using var cap = Rounded(knob, 1.2f);
                    g.FillPath(solid, cap);
                }
                break;
            }
            case BarGlyph.Minimize:
                g.DrawLine(pen, cx - 5, cy, cx + 5, cy);
                break;
            case BarGlyph.Maximize:
                using (var box = Rounded(new RectangleF(cx - 4.5f, cy - 4.5f, 9, 9), 1.5f)) g.DrawPath(pen, box);
                break;
            case BarGlyph.Restore:
                using (var back = new GraphicsPath())
                {
                    back.AddLines(new[] { new PointF(cx - 2f, cy - 4.5f), new PointF(cx + 4.5f, cy - 4.5f), new PointF(cx + 4.5f, cy + 2f) });
                    g.DrawPath(pen, back);
                }
                using (var box = Rounded(new RectangleF(cx - 4.5f, cy - 2f, 7, 7), 1.5f)) g.DrawPath(pen, box);
                break;
            case BarGlyph.Close:
                g.DrawLine(pen, cx - 4.5f, cy - 4.5f, cx + 4.5f, cy + 4.5f);
                g.DrawLine(pen, cx + 4.5f, cy - 4.5f, cx - 4.5f, cy + 4.5f);
                break;
        }
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>
/// Drives the meters at the chosen frame rate. A WinForms timer can't: its WM_TIMER ticks are rounded to the
/// 15.6 ms system tick (about 64 fps at best). This thread sleeps on a high-resolution waitable timer and posts
/// one frame at a time to the UI thread, never queuing a new frame while the last one is still drawing.
/// </summary>
internal sealed class FrameClock : IDisposable
{
    private readonly Control _owner;
    private readonly Action _frame;
    private readonly Thread _thread;
    private readonly IntPtr _waitTimer;
    private volatile bool _running = true;
    private volatile int _pending;
    private volatile int _intervalMicroseconds = 16_667;

    public FrameClock(Control owner, Action frame)
    {
        _owner = owner;
        _frame = frame;
        _waitTimer = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
        _thread = new Thread(Run) { IsBackground = true, Name = "SPECTRM frame clock", Priority = ThreadPriority.AboveNormal };
    }

    /// <summary>Frames per second; 0 = as fast as frames can be drawn.</summary>
    public int FramesPerSecond { set => _intervalMicroseconds = value <= 0 ? 0 : 1_000_000 / value; }

    public void Start() => _thread.Start();

    private void Run()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var next = 0.0;
        while (_running)
        {
            var interval = _intervalMicroseconds / 1000.0;
            var now = clock.Elapsed.TotalMilliseconds;
            if (interval > 0)
            {
                next += interval;
                if (next < now - interval) next = now + interval; // fell behind (busy PC, window dragged): don't burst
                var wait = next - clock.Elapsed.TotalMilliseconds;
                if (wait > 0) Sleep(wait);
            }
            else Sleep(1);
            if (!_running) break;
            if (Interlocked.CompareExchange(ref _pending, 1, 0) != 0) continue; // previous frame still drawing
            try
            {
                if (!_owner.IsHandleCreated || _owner.IsDisposed) { _pending = 0; continue; }
                _owner.BeginInvoke(() =>
                {
                    try { _frame(); }
                    finally { _pending = 0; }
                });
            }
            catch (InvalidOperationException) { _pending = 0; }
        }
    }

    private void Sleep(double milliseconds)
    {
        if (_waitTimer == IntPtr.Zero) { Thread.Sleep(Math.Max(1, (int)milliseconds)); return; }
        var due = -(long)(milliseconds * 10_000); // relative, in 100 ns units
        if (SetWaitableTimer(_waitTimer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            WaitForSingleObject(_waitTimer, 1000);
    }

    public void Dispose()
    {
        _running = false;
        if (_thread.IsAlive) _thread.Join(200);
        if (_waitTimer != IntPtr.Zero) CloseHandle(_waitTimer);
    }

    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x1F0003;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, bool resume);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
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
        m.Result = HtTransparent;
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
    private static Color Ink => Palette.Ink;
    private static Color Muted => Palette.Label;

    private readonly AnalysisEngine _analysis = new();
    private readonly Label _status = new();
    private readonly List<MeterChart> _charts = new();
    private FrameClock? _frameClock;
    private readonly System.Windows.Forms.Timer _deviceTimer = new() { Interval = 1500 };
    private readonly System.Windows.Forms.Timer _topBarTimer = new() { Interval = 30 };
    private readonly BufferedPanel _topBar = new();
    private readonly BarButton _settingsButton = new();
    private readonly BarButton _pinButton = new();
    private readonly BarButton _layoutButton = new();
    private BufferedTableLayoutPanel? _chartGrid;
    private bool _vertical;
    private bool _pinned;
    private Size? _horizontalSize;
    private Size? _verticalSize;
    private static readonly Size HorizontalMinimum = new(900, 100);
    private static readonly Size VerticalMinimum = new(320, 640);
    private readonly BarButton _minimizeButton = new();
    private readonly BarButton _maximizeButton = new();
    private readonly BarButton _closeButton = new();
    private readonly ToolTip _statusTooltip = new();
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
        Palette.Apply(_theme); // before any control is built, so everything starts in the saved theme
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
        _settingsOverlay.CurrentTheme = Array.IndexOf(ColorTheme.All, _theme);
        Palette.Changed += ApplyThemeColors;
        FormClosed += (_, _) => Palette.Changed -= ApplyThemeColors;
        _deviceTimer.Tick += (_, _) => FollowDefaultOutput();
        _topBarTimer.Tick += (_, _) => UpdateTopBarVisibility();
        _frameClock = new FrameClock(this, RenderFrame);
        ConfigureFrameRate();
        HandleCreated += (_, _) => _frameClock.Start();
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
        ConfigureWindowButton(_settingsButton, BarGlyph.Options, "Options (Alt+S)");
        _settingsButton.AccessibleDescription = "Opens options: frame rate, layout and always on top. Keyboard shortcut Alt+S.";
        _settingsButton.Click += (_, _) => OpenSettings();

        ConfigureWindowButton(_pinButton, BarGlyph.Pin, "Pin: always stay on top (Alt+P)");
        _pinButton.Click += (_, _) => SetPinned(!_pinned);

        ConfigureWindowButton(_layoutButton, BarGlyph.Layout, "Switch layout (Alt+L)");
        _layoutButton.Click += (_, _) => SetVertical(!_vertical);

        foreach (var button in new[] { _layoutButton, _pinButton, _settingsButton })
        {
            button.Size = new Size(32, TopBarHeight);
            button.Top = 0;
        }

        ConfigureWindowButton(_minimizeButton, BarGlyph.Minimize, "Minimize");
        ConfigureWindowButton(_maximizeButton, BarGlyph.Maximize, "Maximize");
        ConfigureWindowButton(_closeButton, BarGlyph.Close, "Close");
        foreach (var button in new[] { _minimizeButton, _maximizeButton, _closeButton })
        {
            button.Size = new Size(44, TopBarHeight);
            button.Top = 0;
            button.WindowControl = true;
        }
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
        _settingsButton.Left = _minimizeButton.Left - ToolGap - _settingsButton.Width;
        _pinButton.Left = _settingsButton.Left - _pinButton.Width;
        _layoutButton.Left = _pinButton.Left - _layoutButton.Width;
        _status.Left = _layoutButton.Left - _status.Width - 8;
        _status.Top = (TopBarHeight - _status.Height) / 2;
        // Only problems are shown (no output, reconnecting, error); hide it too rather than run into the logo.
        _status.Visible = _statusProblem && _status.Left > TopBarLogoRight + 16;
        _topBar.Invalidate();
    }

    /// <summary>Space between the tool buttons and the window controls, with a short divider in the middle.</summary>
    private const int ToolGap = 13;

    private void PaintTopBar(Graphics graphics)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = UiFonts.Rendering;
        using var hairline = new Pen(Palette.Line);
        graphics.DrawLine(hairline, 0, _topBar.Height - 1, _topBar.Width, _topBar.Height - 1);
        var divider = _minimizeButton.Left - ToolGap / 2 - 1;
        graphics.DrawLine(hairline, divider, TopBarHeight / 2 - 6, divider, TopBarHeight / 2 + 6);

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

    private void ConfigureWindowButton(BarButton button, BarGlyph glyph, string tooltip)
    {
        button.Glyph = glyph;
        button.Size = new Size(44, TopBarHeight);
        button.Top = 0;
        button.BackColor = Background;
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

        if (_draggedChart is not null && keyData == Keys.Escape)
        {
            EndPanelDrag(cancel: true); // put the panels back where they were
            return true;
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
            m.Result = 1;
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
                if (top && left) { m.Result = HtTopLeft; return; }
                if (top && right) { m.Result = HtTopRight; return; }
                if (bottom && left) { m.Result = HtBottomLeft; return; }
                if (bottom && right) { m.Result = HtBottomRight; return; }
                if (left) { m.Result = HtLeft; return; }
                if (right) { m.Result = HtRight; return; }
                if (top) { m.Result = HtTop; return; }
                if (bottom) { m.Result = HtBottom; return; }
            }

            if (_topBar.Visible && _topBar.Bounds.Contains(point))
            {
                var barPoint = _topBar.PointToClient(screenPoint);
                if (_topBar.GetChildAtPoint(barPoint) is not Button)
                {
                    m.Result = HtCaption;
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
        _maximizeButton.Glyph = WindowState == FormWindowState.Maximized ? BarGlyph.Restore : BarGlyph.Maximize;
        _maximizeButton.Invalidate();
        _statusTooltip.SetToolTip(_maximizeButton, WindowState == FormWindowState.Maximized ? "Restore" : "Maximize");
    }

    private void BeginWindowDrag(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || WindowState == FormWindowState.Maximized) return;
        _isWindowDragging = true;
        try
        {
            ReleaseCapture();
            SendMessage(Handle, WmNcLButtonDown, HtCaption, IntPtr.Zero);
        }
        finally
        {
            _isWindowDragging = false;
        }
    }

    // Default panel order, and the share of the strip each kind of meter gets (it keeps its share when moved).
    private static readonly ChartKind[] DefaultPanelOrder = { ChartKind.Spectrogram, ChartKind.Waveform, ChartKind.LufsMeter, ChartKind.Vectorscope, ChartKind.VuMeter, ChartKind.Oscilloscope, ChartKind.Spectrum };
    private static readonly float[] HorizontalWeights = { 18F, 12.5F, 6.5F, 13F, 16F, 10F, 24F };
    private static readonly float[] VerticalWeights = { 16F, 11F, 9F, 16F, 18F, 11F, 19F };
    private ChartKind[] _panelOrder = DefaultPanelOrder.ToArray();

    private float WeightOf(ChartKind kind) => (_vertical ? VerticalWeights : HorizontalWeights)[Array.IndexOf(DefaultPanelOrder, kind)];

    private Control BuildCharts()
    {
        _chartGrid = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Background,
            Padding = Padding.Empty,
        };
        foreach (var kind in _panelOrder)
        {
            var chart = new MeterChart(kind) { Dock = DockStyle.Fill, Margin = Padding.Empty };
            // Dragging a panel's grip (or Ctrl+drag anywhere on it) rearranges panels; any other drag moves the window.
            chart.MouseDown += (_, e) =>
            {
                if (e.Button == MouseButtons.Left && (chart.IsOverGrip(e.Location) || (ModifierKeys & Keys.Control) != 0))
                    StartPanelDrag(chart, e.Location);
                else
                    BeginWindowDrag(e);
            };
            chart.MouseMove += (_, e) => { if (_draggedChart == chart) PanelDragMove(chart.PointToScreen(e.Location)); };
            chart.MouseUp += (_, _) => { if (_draggedChart == chart) EndPanelDrag(cancel: false); };
            chart.MouseCaptureChanged += (_, _) => { if (_draggedChart == chart && !chart.Capture) EndPanelDrag(cancel: false); };
            _charts.Add(chart);
        }
        ApplyChartLayout();
        return _chartGrid;
    }

    // ---------- Rearranging panels ----------

    private MeterChart? _draggedChart;
    private float _dragGrabOffset;              // where in the panel it was grabbed, along the strip
    private ChartKind[] _orderBeforeDrag = Array.Empty<ChartKind>();

    private void StartPanelDrag(MeterChart chart, Point grabPoint)
    {
        _draggedChart = chart;
        _orderBeforeDrag = _charts.Select(c => c.Kind).ToArray();
        _dragGrabOffset = _vertical ? grabPoint.Y : grabPoint.X;
        chart.Dragging = true;
        chart.Capture = true;
    }

    /// <summary>
    /// Moves the dragged panel past a neighbour once the panel's centre (following the pointer) crosses the
    /// neighbour's centre. Panels have different widths, so comparing centres keeps it from flipping back and forth.
    /// </summary>
    private void PanelDragMove(Point screenPoint)
    {
        if (_draggedChart is not { } chart || _chartGrid is null) return;
        var pointer = _chartGrid.PointToClient(screenPoint);
        float Start(Control c) => _vertical ? c.Top : c.Left;
        float Size(Control c) => _vertical ? c.Height : c.Width;
        var center = (_vertical ? pointer.Y : pointer.X) - _dragGrabOffset + Size(chart) / 2f;
        for (var guard = 0; guard < _charts.Count; guard++)
        {
            var index = _charts.IndexOf(chart);
            var target = index;
            if (index < _charts.Count - 1 && center > Start(_charts[index + 1]) + Size(_charts[index + 1]) / 2f) target = index + 1;
            else if (index > 0 && center < Start(_charts[index - 1]) + Size(_charts[index - 1]) / 2f) target = index - 1;
            if (target == index) break;
            (_charts[index], _charts[target]) = (_charts[target], _charts[index]);
            UpdateChartPositions();
        }
    }

    private void EndPanelDrag(bool cancel)
    {
        if (_draggedChart is not { } chart) return;
        _draggedChart = null;
        chart.Dragging = false;
        if (chart.Capture) chart.Capture = false;
        if (cancel)
        {
            _charts.Sort((a, b) => Array.IndexOf(_orderBeforeDrag, a.Kind).CompareTo(Array.IndexOf(_orderBeforeDrag, b.Kind)));
            UpdateChartPositions();
            return;
        }
        var order = _charts.Select(c => c.Kind).ToArray();
        if (order.SequenceEqual(_panelOrder)) return;
        _panelOrder = order;
        SaveSettings();
    }

    /// <summary>Moves panels to their cells in the current order without removing them (so a drag keeps the mouse).</summary>
    private void UpdateChartPositions()
    {
        if (_chartGrid is null) return;
        var grid = _chartGrid;
        grid.SuspendLayout();
        for (var i = 0; i < _charts.Count; i++)
        {
            var weight = WeightOf(_charts[i].Kind);
            if (_vertical) grid.RowStyles[i].Height = weight; else grid.ColumnStyles[i].Width = weight;
            grid.SetCellPosition(_charts[i], new TableLayoutPanelCellPosition(_vertical ? 0 : i, _vertical ? i : 0));
        }
        grid.ResumeLayout(true);
    }

    /// <summary>Places the meters in one row (horizontal) or one column (vertical).</summary>
    private void ApplyChartLayout()
    {
        if (_chartGrid is null) return;
        var grid = _chartGrid;
        grid.SuspendLayout();
        grid.Controls.Clear();
        grid.ColumnStyles.Clear();
        grid.RowStyles.Clear();
        grid.ColumnCount = _vertical ? 1 : _charts.Count;
        grid.RowCount = _vertical ? _charts.Count : 1;
        if (_vertical)
        {
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (var chart in _charts) grid.RowStyles.Add(new RowStyle(SizeType.Percent, WeightOf(chart.Kind)));
        }
        else
        {
            foreach (var chart in _charts) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, WeightOf(chart.Kind)));
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
        _pinButton.Active = _pinned;
        var tip = _pinned ? "Unpin: stop staying on top (Alt+P)" : "Pin: always stay on top (Alt+P)";
        _statusTooltip.SetToolTip(_pinButton, tip);
        _pinButton.AccessibleName = tip;
    }

    private void UpdateLayoutButton()
    {
        var tip = _vertical ? "Switch to horizontal layout (Alt+L)" : "Switch to vertical layout (Alt+L)";
        _statusTooltip.SetToolTip(_layoutButton, tip);
        _layoutButton.AccessibleName = tip;
        _layoutButton.Vertical = _vertical;
        _settingsOverlay.Vertical = _vertical;
    }

    /// <summary>
    /// Reads settings.txt: "key=value" lines (fps, layout, pinned, order, theme). A file holding only a number is the
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
                    case "order" when TryParsePanelOrder(value, out var order): _panelOrder = order; break;
                    case "theme" when ColorTheme.All.Any(theme => theme.Id == value): _theme = ColorTheme.ById(value); break;
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
                $"order={string.Join(",", _panelOrder)}",
                $"theme={_theme.Id}",
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

    /// <summary>Reads "spectrogram,waveform,..." — must list every panel exactly once.</summary>
    private static bool TryParsePanelOrder(string value, out ChartKind[] order)
    {
        order = Array.Empty<ChartKind>();
        var parsed = new List<ChartKind>();
        foreach (var name in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Enum.TryParse<ChartKind>(name, ignoreCase: true, out var kind) || !Enum.IsDefined(kind) || parsed.Contains(kind)) return false;
            parsed.Add(kind);
        }
        if (parsed.Count != DefaultPanelOrder.Length) return false;
        order = parsed.ToArray();
        return true;
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
        if (_frameClock is not null) _frameClock.FramesPerSecond = _targetFps;
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
        _settingsOverlay.ThemeChosen += index => SetTheme(ColorTheme.All[index]); // stays open so themes can be compared
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

    /// <summary>One frame from the frame clock: update the meters, then paint them right away, so the next frame
    /// isn't posted until this one is on screen (posted messages otherwise starve WM_PAINT at high rates).</summary>
    private void RenderFrame()
    {
        RefreshMeters();
        if (IsHandleCreated && WindowState != FormWindowState.Minimized)
            RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, RedrawUpdateNow | RedrawAllChildren);
    }

    private const uint RedrawUpdateNow = 0x0100;
    private const uint RedrawAllChildren = 0x0080;

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr window, IntPtr rect, IntPtr region, uint flags);

    private void RefreshMeters()
    {
        if (_disposed) return;
        if (_lastOutputError is not null)
            SetStatus($"OUTPUT ERROR: {_lastOutputError}");
        _analysis.FillSilenceIfStalled(); // so meters fall back when playback stops instead of freezing
        var snapshot = _analysis.GetSnapshot();
        if (!snapshot.HasSignal && _targetFps == 0)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastIdleRefresh).TotalMilliseconds < 33) return;
            _lastIdleRefresh = now;
        }
        if (snapshot.HasSignal && _lastDisplayedGeneration == snapshot.Generation)
        {
            // No new audio yet (Windows delivers it every 10 ms), but the needle and peak holds still move in time.
            foreach (var chart in _charts) chart.Invalidate();
            return;
        }
        _lastDisplayedGeneration = snapshot.Generation;
        foreach (var chart in _charts) chart.SetSnapshot(snapshot);
    }

    private void SetStatus(string text, bool active = false)
    {
        _status.Text = active
            ? ""
            : text.StartsWith("DEFAULT OUTPUT UNAVAILABLE", StringComparison.OrdinalIgnoreCase)
                ? "● NO OUTPUT"
                : text.StartsWith("RECONNECTING", StringComparison.OrdinalIgnoreCase)
                    ? "● RECONNECTING"
                    : text.StartsWith("OUTPUT ERROR", StringComparison.OrdinalIgnoreCase)
                        ? "● ERROR"
                        : "● OFFLINE";
        _statusText = text;
        _statusActive = active;
        _status.ForeColor = active ? Palette.Accent : text.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || text.Contains("UNAVAILABLE", StringComparison.OrdinalIgnoreCase) ? Palette.Highlight : Muted;
        _statusTooltip.SetToolTip(_status, text);
        _statusProblem = !active && !_status.Text.EndsWith("OFFLINE", StringComparison.Ordinal);
        LayoutTopBar();
    }

    private bool _statusProblem;

    private string _statusText = "OFFLINE";
    private bool _statusActive;
    private ColorTheme _theme = ColorTheme.All[0];

    private void SetTheme(ColorTheme theme)
    {
        _theme = theme;
        Palette.Apply(theme);
        _settingsOverlay.CurrentTheme = Array.IndexOf(ColorTheme.All, theme);
        SaveSettings();
    }

    /// <summary>Re-colours the controls that copy palette colours when they are set up; everything else repaints.</summary>
    private void ApplyThemeColors()
    {
        UpdatePinButton();
        SetStatus(_statusText, _statusActive);
        _topBar.Invalidate(true);
        _settingsOverlay.Invalidate();
        foreach (var chart in _charts) chart.OnThemeChanged();
        _splash?.Invalidate();
    }

    private void ShowError(string message)
    {
        if (!IsDisposed) MessageBox.Show(this, message, "SPECTRM", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void DisposeAudio()
    {
        _disposed = true;
        _frameClock?.Dispose();
        _frameClock = null;
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
        _deviceTimer.Dispose();
        _topBarTimer.Dispose();
        _glideTimer.Dispose();
    }
}

internal sealed class SettingsOverlay : Control
{
    /// <summary>Smallest client area for the wide (strip) arrangement.</summary>
    public static readonly Size WideMinimum = new(1700, 120);

    // Every tile uses the same template: title top-left, description under it, key hint in the bottom-right corner.
    private const int TileWidth = 116;
    private const int TileHeight = 50;
    private const int LabelHeight = 16;
    private const int TilePadding = 13;
    /// <summary>Smallest client area for the tall (column) arrangement.</summary>
    public static readonly Size TallMinimum = new(300, 620);
    private const int ThemeTileWidth = 104;

    private enum ItemKind { Fps, Layout, Pin, Theme, Close }
    private sealed record Item(ItemKind Kind, int Value, string Key);

    private const int TileGap = 6;
    private const int GroupGap = 22;

    // Read from the active theme each time, so the panel recolours as soon as a theme is picked.
    private static Color Ink => Palette.Ink;
    private static Color Muted => Palette.Label;
    private static Color TileFill => Palette.Mix(Color.Black, Palette.Track, 0.7);
    private static Color TileHover => Palette.Hover;
    private static Color TileSelected => Palette.Mix(Palette.Track, Palette.Box, 0.6);
    private static Color TileBorder => Palette.Line;
    private static Color TileBorderStrong => Palette.Mix(Palette.Line, Palette.Accent, 0.35);
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
    private static readonly Item[] ThemeItems = Enumerable.Range(0, ColorTheme.All.Length).Select(i => new Item(ItemKind.Theme, i, "")).ToArray();
    private static readonly Item CloseItem = new(ItemKind.Close, 0, "");

    private readonly List<(Rectangle Bounds, Item Item)> _items = new();
    private readonly List<(PointF Position, string Text)> _groupLabels = new();
    private Point _headingOrigin;
    private bool _wide = true;
    private int _hover = -1;
    private int _currentFps = 60;
    private bool _vertical;
    private bool _pinned;
    private int _currentTheme;

    public event Action<int>? FpsChosen;
    public event Action<int>? ThemeChosen;
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

    public int CurrentTheme
    {
        get => _currentTheme;
        set { _currentTheme = value; Invalidate(); }
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
        var total = textWidth + GroupGap + (4 * TileWidth + 3 * TileGap) + GroupGap + (2 * TileWidth + TileGap) + GroupGap + TileWidth
            + GroupGap + (ThemeItems.Length * ThemeTileWidth + (ThemeItems.Length - 1) * TileGap);
        var left = Math.Max(16, (width - total) / 2);
        var blockTop = (height - (LabelHeight + TileHeight)) / 2;
        _headingOrigin = new Point(left, blockTop);
        var top = blockTop + LabelHeight;
        var x = left + textWidth + GroupGap;

        void Group(string label, IEnumerable<Item> items, int tileWidth = TileWidth)
        {
            _groupLabels.Add((new PointF(x, blockTop), label));
            foreach (var item in items)
            {
                _items.Add((new Rectangle(x, top, tileWidth, TileHeight), item));
                x += tileWidth + TileGap;
            }
            x += GroupGap - TileGap;
        }

        Group("FRAME RATE", FpsItems);
        Group("LAYOUT", LayoutItems);
        Group("WINDOW", new[] { PinItem });
        Group("THEMES", ThemeItems, ThemeTileWidth);
    }

    private void ArrangeTall(int width, int height)
    {
        const int tileHeight = TileHeight, labelHeight = LabelHeight, sectionGap = 16, headingHeight = 84;
        var contentWidth = Math.Min(width - 32, 320);
        var left = (width - contentWidth) / 2;
        var half = (contentWidth - TileGap) / 2;
        var themeRows = (ThemeItems.Length + 1) / 2;
        var blockHeight = headingHeight
            + labelHeight + tileHeight * 2 + TileGap + sectionGap
            + labelHeight + tileHeight + sectionGap
            + labelHeight + tileHeight + sectionGap
            + labelHeight + themeRows * tileHeight + (themeRows - 1) * TileGap;
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
        y += tileHeight + sectionGap;

        _groupLabels.Add((new PointF(left, y), "THEMES"));
        y += labelHeight;
        for (var i = 0; i < ThemeItems.Length; i++)
            _items.Add((new Rectangle(left + (i % 2) * (half + TileGap), y + (i / 2) * (tileHeight + TileGap), half, tileHeight), ThemeItems[i]));
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
            case ItemKind.Theme: ThemeChosen?.Invoke(item.Value); break;
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
            Keys.T => ThemeItems[(_currentTheme + 1) % ThemeItems.Length],                         // next theme
            Keys.Shift | Keys.T => ThemeItems[(_currentTheme + ThemeItems.Length - 1) % ThemeItems.Length], // previous
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
        ItemKind.Theme => item.Value == _currentTheme,
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
        ItemKind.Theme => (ColorTheme.All[item.Value].Name.ToUpperInvariant(), ""),
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
        graphics.DrawString("P pin  ·  T theme  ·  Esc close", HintFont, mutedBrush, x, y + LabelHeight + 36);

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
            using (var border = new Pen(selected ? Palette.Accent : hovered ? TileBorderStrong : TileBorder))
                graphics.DrawPath(border, path);
            if (selected)
                graphics.FillRectangle(highlightBrush, tile.Left + 1, tile.Top + 11, 3, tile.Height - 22);

            var (title, detail) = Describe(item);
            graphics.DrawString(title, TileTitleFont, selected ? accentBrush : inkBrush, tile.Left + TilePadding, tile.Top + 8);
            if (item.Kind == ItemKind.Theme)
            {
                DrawThemePreview(graphics, ColorTheme.All[item.Value], tile.Left + TilePadding, tile.Top + 29, tile.Right - TilePadding);
                continue;
            }
            graphics.DrawString(detail, TileDetailFont, mutedBrush, tile.Left + TilePadding, tile.Top + 27);

            // Key hint: a small keycap pinned to the bottom-right corner, level with the description.
            var keySize = graphics.MeasureString(item.Key, TagFont);
            var cap = new RectangleF(tile.Right - 8 - Math.Max(13, keySize.Width + 4), tile.Bottom - 8 - 13, Math.Max(13, keySize.Width + 4), 13);
            using (var capPath = RoundedRectangle(Rectangle.Round(cap), 3))
            using (var capPen = new Pen(selected ? TileBorderStrong : TileBorder))
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

    /// <summary>A theme's look in miniature: its three accent dots, then a strip of its spectrogram colours.</summary>
    private static void DrawThemePreview(Graphics graphics, ColorTheme theme, float left, float top, float right)
    {
        const float dot = 9, gap = 4;
        var x = left;
        foreach (var color in new[] { theme.Accent, theme.Secondary, theme.Highlight })
        {
            using var brush = new SolidBrush(color);
            graphics.FillEllipse(brush, x, top, dot, dot);
            x += dot + gap;
        }
        var strip = new RectangleF(x + 2, top + 1.5f, Math.Max(8, right - x - 2), dot - 3);
        using var gradient = new LinearGradientBrush(new RectangleF(strip.X - 1, strip.Y, strip.Width + 2, strip.Height), Color.Black, Color.White, 0f)
        {
            InterpolationColors = new ColorBlend
            {
                Colors = theme.Spectrogram,
                Positions = Enumerable.Range(0, theme.Spectrogram.Length).Select(i => i / (float)(theme.Spectrogram.Length - 1)).ToArray(),
            },
        };
        using var stripPath = RoundedRectangle(Rectangle.Round(strip), 2);
        graphics.FillPath(gradient, stripPath);
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
    public double MomentaryLufs { get; set; } = -100;
    /// <summary>K-weighted loudness over ~25 ms (LUFS units): what the VU needle's movement follows.</summary>
    public double FastLufs { get; set; } = -100;
    public double ShortTermLufs { get; set; } = -100;
    public int SampleRate { get; set; }
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
    private double _lufsEnergy;
    // K-weighted loudness with only a 25 ms average: drives the VU needle, whose own mass and damping do the rest.
    private const double FastLoudnessSeconds = 0.025;
    private double _fastLufsEnergy;
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
    // Windows loopback capture delivers nothing while no app is playing, which would freeze the meters on the last
    // loud value. When the stream stalls, the gap is filled with digital silence so loudness falls as it really does.
    private const double StallSeconds = 0.25;
    private readonly System.Diagnostics.Stopwatch _sinceLastCapture = new();
    private long _silenceFramesAdded;
    private int _lastChannels;
    private float[] _silence = Array.Empty<float>();

    public void PushInterleaved(ReadOnlySpan<float> samples, int channels, int sampleRate)
    {
        if (channels < 1) return;
        lock (_gate)
        {
            _sinceLastCapture.Restart();
            _silenceFramesAdded = 0;
            _lastChannels = channels;
            PushLocked(samples, channels, sampleRate);
        }
    }

    /// <summary>Called on every display refresh: if capture has gone quiet, feed silence for the time that has passed.</summary>
    public void FillSilenceIfStalled()
    {
        lock (_gate)
        {
            if (!_sinceLastCapture.IsRunning || _lufsFilterSampleRate <= 0 || _lastChannels < 1) return;
            var elapsed = _sinceLastCapture.Elapsed.TotalSeconds;
            if (elapsed < StallSeconds) return;
            // Cap the backlog so a long pause (sleep, device change) doesn't stall the UI thread.
            var due = (long)(elapsed * _lufsFilterSampleRate) - _silenceFramesAdded;
            var frames = (int)Math.Min(due, _lufsFilterSampleRate);
            if (frames <= 0) return;
            if (due > _lufsFilterSampleRate) _silenceFramesAdded += due - frames; // skip what we won't replay
            var length = frames * _lastChannels;
            if (_silence.Length < length) _silence = new float[length];
            PushLocked(_silence.AsSpan(0, length), _lastChannels, _lufsFilterSampleRate);
            _silenceFramesAdded += frames;
        }
    }

    private void PushLocked(ReadOnlySpan<float> samples, int channels, int sampleRate)
    {
        if (_lufsFilterSampleRate != sampleRate)
            ConfigureLufsFilters(sampleRate);
        var frames = samples.Length / channels;
        if (frames == 0) return;
        var lufsBlockEnergy = 0.0;
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
            _left[_writePosition] = left;
            _right[_writePosition] = right;
            _writePosition = (_writePosition + 1) % RingSize;
        }
        _totalFrames += frames;
        var smoothing = Math.Exp(-(double)frames / (sampleRate * LevelSmoothingSeconds));
        _lufsEnergy = smoothing * _lufsEnergy + (1 - smoothing) * lufsBlockEnergy / frames;
        var fastSmoothing = Math.Exp(-(double)frames / (sampleRate * FastLoudnessSeconds));
        _fastLufsEnergy = fastSmoothing * _fastLufsEnergy + (1 - fastSmoothing) * lufsBlockEnergy / frames;
        _generation++;
    }

    public AnalysisSnapshot GetSnapshot()
    {
        long generation;
        double lufsEnergy, fastLufsEnergy, shortTermLufsEnergy;
        int sampleRate;
        var left = _snapshot.Left;
        var right = _snapshot.Right;
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
            lufsEnergy = _lufsEnergy;
            fastLufsEnergy = _fastLufsEnergy;
            sampleRate = _lufsFilterSampleRate;
            shortTermLufsEnergy = 0;
            for (var i = 0; i < _shortTermBlockCount; i++)
                shortTermLufsEnergy += _shortTermBlockEnergy[i];
            if (_shortTermBlockCount > 0) shortTermLufsEnergy /= _shortTermBlockCount;

            // The newest FftSize frames of the ring, oldest first, right-aligned (zeros before them at start-up).
            var available = (int)Math.Min(FftSize, _totalFrames);
            var start = (_writePosition - available + RingSize) % RingSize;
            var offset = FftSize - available;
            Array.Clear(left, 0, offset);
            Array.Clear(right, 0, offset);
            var firstPart = Math.Min(available, RingSize - start);
            Array.Copy(_left, start, left, offset, firstPart);
            Array.Copy(_right, start, right, offset, firstPart);
            Array.Copy(_left, 0, left, offset + firstPart, available - firstPart);
            Array.Copy(_right, 0, right, offset + firstPart, available - firstPart);
        }

        for (var i = 0; i < FftSize; i++)
            _combined[i] = (left[i] + right[i]) * 0.5f;
        CalculateSpectrum(_combined, _fftReal, _fftImaginary, _snapshot.SpectrumDb);
        _snapshot.HasSignal = true;
        _snapshot.Generation = generation;
        _snapshot.MomentaryLufs = lufsEnergy > 1e-10 ? -0.691 + 10 * Math.Log10(lufsEnergy) : -100;
        _snapshot.FastLufs = fastLufsEnergy > 1e-10 ? -0.691 + 10 * Math.Log10(fastLufsEnergy) : -100;
        _snapshot.SampleRate = sampleRate;
        _snapshot.ShortTermLufs = shortTermLufsEnergy > 1e-10 ? -0.691 + 10 * Math.Log10(shortTermLufsEnergy) : -100;
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
            _sinceLastCapture.Reset(); // no silence filling until a device delivers audio again
            _silenceFramesAdded = 0;
            _generation++;
            _snapshotGeneration = -1;
            _snapshot.HasSignal = false;
            _lufsEnergy = 0;
            _fastLufsEnergy = 0;
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
        _fastLufsEnergy = 0;
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

    // ITU-R BS.1770 K-weighting, using the bilinear-transform design of the EBU reference implementation
    // (libebur128). At 48 kHz it reproduces the standard's published coefficients exactly, and it adapts
    // correctly to 44.1 kHz, 96 kHz and other rates.
    private static BiquadCoefficients BuildHighShelf(int sampleRate, double frequency, double gainDb, double q)
    {
        var k = Math.Tan(Math.PI * frequency / sampleRate);
        var vh = Math.Pow(10, gainDb / 20);
        var vb = Math.Pow(vh, 0.4996667741545416);
        var a0 = 1 + k / q + k * k;
        return new BiquadCoefficients(
            (vh + vb * k / q + k * k) / a0,
            2 * (k * k - vh) / a0,
            (vh - vb * k / q + k * k) / a0,
            2 * (k * k - 1) / a0,
            (1 - k / q + k * k) / a0);
    }

    private static BiquadCoefficients BuildHighPass(int sampleRate, double frequency, double q)
    {
        var k = Math.Tan(Math.PI * frequency / sampleRate);
        var a0 = 1 + k / q + k * k;
        return new BiquadCoefficients(1, -2, 1, 2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0);
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
    // The loudness meter's scale is EBU short-term loudness itself, in LUFS, evenly spaced in dB so the needle
    // sits exactly on the value it reads. Above the -9 LUFS reference (loud modern masters) the scale turns red.
    private const double VuReferenceLufs = -9;
    private const double VuScaleMin = -30;
    private const double VuScaleMax = 0;
    private static readonly int[] VuMajorMarks = { -30, -24, -18, -15, -12, -9, -6, -3, 0 };
    private static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    private static Color Ink => Palette.Ink;
    private static readonly Pen GridPen = new(Palette.Grid, 1F);
    private static readonly Pen SpectrumPen = new(Palette.Accent, 1.2F);
    private static readonly Pen ScopePen = new(Palette.Secondary, 1F);
    private static readonly Pen HoverPen = new(Color.FromArgb(80, Palette.Accent), 1F);
    private static readonly Pen VuInkPen = new(Ink, 2.4F);
    private static readonly Pen VuRedPen = new(Palette.Over, 2.4F);
    private static readonly Pen VuBaselinePen = new(Ink, 2F);
    private static readonly Pen VuMinorPen = new(Color.FromArgb(150, Ink), 1.2F);
    private static readonly Pen VuRedMinorPen = new(Color.FromArgb(170, Palette.Over), 1.2F);
    private static readonly Pen NeedlePen = new(Palette.Needle, 2.2F);
    private static readonly SolidBrush InkBrush = new(Ink);
    private static readonly SolidBrush LabelBrush = new(Palette.Label);
    private static readonly SolidBrush AccentBrush = new(Palette.Accent);
    private static readonly SolidBrush TrackBrush = new(Palette.Track);
    private static readonly SolidBrush BoxBrush = new(Palette.Box);
    private static readonly SolidBrush VectorBrush = new(Color.FromArgb(160, Palette.Accent));
    private static readonly SolidBrush VuRedBrush = new(Palette.Over);
    private static readonly SolidBrush LevelBrush = new(Palette.Secondary);
    private static readonly Pen ScaleTickPen = new(Palette.Label, 1F);
    private static readonly Font SmallFont = new(UiFonts.Numbers, 7F);
    private static readonly Font ValueFont = new(UiFonts.Numbers, 8.5F);
    private static readonly int[] InfernoLut = BuildInfernoLut();

    static MeterChart() => Palette.Changed += RefreshThemeStyles;

    /// <summary>Re-colours the shared pens, brushes and spectrogram colour table for the active theme.</summary>
    private static void RefreshThemeStyles()
    {
        GridPen.Color = Palette.Grid;
        SpectrumPen.Color = Palette.Accent;
        ScopePen.Color = Palette.Secondary;
        HoverPen.Color = Color.FromArgb(80, Palette.Accent);
        VuInkPen.Color = Ink;
        VuRedPen.Color = Palette.Over;
        VuBaselinePen.Color = Ink;
        VuMinorPen.Color = Color.FromArgb(150, Ink);
        VuRedMinorPen.Color = Color.FromArgb(170, Palette.Over);
        NeedlePen.Color = Palette.Needle;
        InkBrush.Color = Ink;
        LabelBrush.Color = Palette.Label;
        AccentBrush.Color = Palette.Accent;
        TrackBrush.Color = Palette.Track;
        BoxBrush.Color = Palette.Box;
        VectorBrush.Color = Color.FromArgb(160, Palette.Accent);
        VuRedBrush.Color = Palette.Over;
        LevelBrush.Color = Palette.Secondary;
        ScaleTickPen.Color = Palette.Label;
        Array.Copy(BuildInfernoLut(), InfernoLut, InfernoLut.Length);
    }

    /// <summary>
    /// Called after the theme changes. The spectrogram and waveform keep their history as finished colours, so
    /// they are cleared and redraw in the new theme within a few seconds; everything else just repaints.
    /// </summary>
    public void OnThemeChanged()
    {
        if (_spectrogramPixels.Length > 0)
        {
            Array.Fill(_spectrogramPixels, unchecked((int)0xFF000000));
            FlushSpectrogram();
        }
        _waveCount = 0;
        Invalidate();
    }

    private readonly ChartKind _kind;
    private AnalysisSnapshot _snapshot = new();
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
    private double _needleAmplitude;
    private double _needleVelocity;
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
            // Both scroll at a fixed speed in real time, independent of the frame rate: each frame adds however
            // many rows/columns of time have passed since the last one.
            if (_kind == ChartKind.Spectrogram)
            {
                var rows = Steps(ref _lastSpectrogramRow, ref _spectrogramCarry, now, SpectrogramRowMilliseconds, 64);
                if (rows > 0) AppendSpectrogramRows(snapshot, rows);
            }
            else if (_kind == ChartKind.Waveform)
            {
                var columns = Steps(ref _lastWaveColumn, ref _waveCarry, now, WaveColumnMilliseconds, 128);
                if (columns > 0) AppendWaveColumns(snapshot, columns);
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

        Invalidate(); // the window's frame clock paces this
    }

    protected override void WndProc(ref Message m)
    {
        if (EdgePassThrough.Handle(this, ref m)) return;
        base.WndProc(ref m);
    }

    public ChartKind Kind => _kind;

    // ---------- Grip for rearranging panels ----------
    // Shown in the top-left corner while the pointer is over the panel. It sits below the 8 px band that reveals
    // the top bar, so reaching for it doesn't pop the bar open over it.
    private static readonly Rectangle GripBounds = new(8, 12, 24, 17);
    private bool _pointerInside;
    private bool _pointerOnGrip;
    private bool _dragging;

    public bool IsOverGrip(Point point) => GripBounds.Contains(point);

    public bool Dragging
    {
        get => _dragging;
        set
        {
            _dragging = value;
            Cursor = value ? Cursors.SizeAll : DefaultCursorFor(_pointerOnGrip);
            Invalidate();
        }
    }

    private Cursor DefaultCursorFor(bool onGrip) =>
        onGrip ? Cursors.SizeAll : _kind == ChartKind.Spectrum ? Cursors.Cross : Cursors.Default;

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _pointerInside = true;
        Invalidate(GripBounds);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var onGrip = IsOverGrip(e.Location);
        if (onGrip != _pointerOnGrip)
        {
            _pointerOnGrip = onGrip;
            if (!_dragging) Cursor = DefaultCursorFor(onGrip);
            Invalidate(GripBounds);
        }
        if (!_pointerInside)
        {
            _pointerInside = true;
            Invalidate(GripBounds);
        }
        if (_kind != ChartKind.Spectrum || _dragging) return;
        _hover = e.Location;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _pointerInside = false;
        _pointerOnGrip = false;
        Invalidate(GripBounds);
        if (_hover is null) return;
        _hover = null;
        Invalidate();
    }

    private void DrawGripAndDragState(Graphics graphics)
    {
        var rect = ClientRectangle;
        if (_dragging)
        {
            // The panel being moved: a soft mint wash and outline.
            using (var wash = new SolidBrush(Color.FromArgb(22, Palette.Accent)))
                graphics.FillRectangle(wash, rect);
            using var outline = new Pen(Palette.Accent, 2F);
            graphics.DrawRectangle(outline, 1, 1, rect.Width - 2, rect.Height - 2);
        }
        if (!_pointerInside && !_dragging) return;

        var smoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var active = _pointerOnGrip || _dragging;
        using (var path = RoundedBox(new RectangleF(GripBounds.X + 0.5f, GripBounds.Y + 0.5f, GripBounds.Width - 1, GripBounds.Height - 1), 4f))
        {
            using (var fill = new SolidBrush(Color.FromArgb(230, active ? Palette.Hover : Palette.Box)))
                graphics.FillPath(fill, path);
            using var border = new Pen(active ? Palette.Accent : Palette.Line);
            graphics.DrawPath(border, path);
        }
        // Six-dot grip.
        using var dots = new SolidBrush(active ? Palette.Accent : Palette.Label);
        var cx = GripBounds.X + GripBounds.Width / 2f;
        var cy = GripBounds.Y + GripBounds.Height / 2f;
        for (var row = -1; row <= 1; row++)
            for (var col = -1; col <= 1; col += 2)
                graphics.FillEllipse(dots, cx + col * 2.6f - 1.4f, cy + row * 4f - 1.4f, 2.8f, 2.8f);
        graphics.SmoothingMode = smoothing;
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
        DrawGripAndDragState(graphics);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _spectrogramBitmap?.Dispose();
        base.Dispose(disposing);
    }

    // ---------- Spectrogram (log-frequency waterfall, newest row at the bottom) ----------

    // Scroll speeds: how much time one spectrogram row / one waveform column stands for. Smaller = faster scrolling
    // and a shorter stretch of time on screen (about 1.4 s and 1.3 s in the default strip).
    private const double SpectrogramRowMilliseconds = 10;
    private const double WaveColumnMilliseconds = 6;
    private double _spectrogramCarry;
    private double _waveCarry;

    /// <summary>How many fixed-length steps of time have passed since the last call (the remainder carries over).</summary>
    private static int Steps(ref DateTime last, ref double carry, DateTime now, double stepMilliseconds, int max)
    {
        if (last == DateTime.MinValue)
        {
            last = now;
            carry = 0;
            return 1;
        }
        carry += (now - last).TotalMilliseconds;
        last = now;
        var steps = (int)(carry / stepMilliseconds);
        carry -= steps * stepMilliseconds;
        if (steps > max)
        {
            steps = max; // after a stall (window dragged, PC busy) don't try to catch up
            carry = 0;
        }
        return steps;
    }

    private void AppendSpectrogramRows(AnalysisSnapshot snapshot, int rows)
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

        // The spectrum is analysed once per frame, so the new rows for this frame share it.
        rows = Math.Min(rows, height);
        Array.Copy(_spectrogramPixels, width * rows, _spectrogramPixels, 0, width * (height - rows));
        var row = width * (height - 1);
        var binWidth = BinWidth(snapshot);
        for (var x = 0; x < width; x++)
        {
            var db = SampleBand(snapshot.SpectrumDb, binWidth, XToFrequency(x / (double)width), XToFrequency((x + 1) / (double)width));
            var level = Math.Pow(Math.Clamp((db + 92) / 80, 0, 1), 1.8);
            _spectrogramPixels[row + x] = InfernoLut[(int)(level * 255)];
        }
        for (var r = 1; r < rows; r++)
            Array.Copy(_spectrogramPixels, row, _spectrogramPixels, row - r * width, width);
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

    /// <summary>
    /// Adds <paramref name="columns"/> waveform columns, each the peak of its own slice of the newest audio
    /// (one column = <see cref="WaveColumnMilliseconds"/>), so faster scrolling shows real detail, not stretched copies.
    /// </summary>
    private void AppendWaveColumns(AnalysisSnapshot snapshot, int columns)
    {
        var length = snapshot.Left.Length;
        if (length == 0) return;
        var sampleRate = snapshot.SampleRate > 0 ? snapshot.SampleRate : 48000;
        var perColumn = Math.Max(1, (int)(sampleRate * WaveColumnMilliseconds / 1000));
        columns = Math.Min(columns, Math.Max(1, length / perColumn));
        var color = WaveColumnColor(snapshot);
        for (var column = 0; column < columns; column++)
        {
            var end = length - (columns - 1 - column) * perColumn;
            var peak = 0f;
            for (var i = Math.Max(0, end - perColumn); i < end; i++)
                peak = Math.Max(peak, Math.Max(Math.Abs(snapshot.Left[i]), Math.Abs(snapshot.Right[i])));
            _wavePeaks[_waveWrite] = Math.Min(1f, peak);
            _waveColors[_waveWrite] = WaveColor(color, peak);
            _waveWrite = (_waveWrite + 1) % WaveformHistory;
            _waveCount = Math.Min(_waveCount + 1, WaveformHistory);
        }
        _hasHistory = true;
    }

    /// <summary>Where the sound's brightness sits (0 = bass-heavy … 1 = bright), from the spectral centre.</summary>
    private static double WaveColumnColor(AnalysisSnapshot snapshot)
    {
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
        return Math.Clamp((centroid - Math.Log2(150)) / (Math.Log2(6000) - Math.Log2(150)), 0, 1);
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

    // ---------- Level (dB) + loudness (LUFS) bars ----------
    // Left bar: sample-peak level in dBFS, PPM-style (instant rise, falls 20 dB/s) with a peak-hold line.
    // Right bar: momentary loudness in LUFS. Both share the 0 to -54 scale on the left.
    private const double PeakFallDbPerSecond = 20;
    private const double PeakHoldSeconds = 1.5;
    private const double PeakWarningDb = -3; // the dB bar turns to the warning colour above this
    private double _peakDb = -100;
    private double _peakHoldDb = -100;
    private DateTime _peakHoldUntil = DateTime.MinValue;
    private DateTime _lastPeakUpdate = DateTime.MinValue;

    /// <summary>Sample peak (dBFS) of the audio that arrived since the last frame, with meter ballistics.</summary>
    private double UpdatePeak(AnalysisSnapshot snapshot)
    {
        var now = DateTime.UtcNow;
        var elapsed = _lastPeakUpdate == DateTime.MinValue ? 0.02 : Math.Clamp((now - _lastPeakUpdate).TotalSeconds, 0.001, 0.25);
        _lastPeakUpdate = now;
        var current = -100.0;
        if (snapshot.HasSignal && snapshot.Left.Length > 0)
        {
            var sampleRate = snapshot.SampleRate > 0 ? snapshot.SampleRate : 48000;
            var count = Math.Clamp((int)(sampleRate * elapsed) + 64, 64, snapshot.Left.Length);
            var peak = 0f;
            for (var i = snapshot.Left.Length - count; i < snapshot.Left.Length; i++)
                peak = Math.Max(peak, Math.Max(Math.Abs(snapshot.Left[i]), Math.Abs(snapshot.Right[i])));
            current = peak > 1e-6f ? 20 * Math.Log10(peak) : -100;
        }
        _peakDb = Math.Max(current, _peakDb - PeakFallDbPerSecond * elapsed);
        if (_peakDb >= _peakHoldDb || now >= _peakHoldUntil)
        {
            _peakHoldDb = _peakDb;
            _peakHoldUntil = now.AddSeconds(PeakHoldSeconds);
        }
        return _peakDb;
    }

    private void DrawLufsMeter(Graphics graphics, Rectangle rect, AnalysisSnapshot snapshot)
    {
        graphics.SmoothingMode = SmoothingMode.None;
        const double range = 54;
        var top = rect.Top + 8f;
        var bottom = rect.Bottom - 6f;
        float Y(double db) => top + (float)(Math.Clamp(-db, 0, range) / range) * (bottom - top);

        // Centre the scale + bars + readouts when the panel is wider than they need (vertical layout).
        const float labelWidth = 12f, tickWidth = 5f, barWidth = 7f, gap = 2f;
        var contentWidth = labelWidth + 2 + tickWidth + 2 + barWidth * 2 + gap + 3 + graphics.MeasureString("-88.8 LUFS", ValueFont).Width + 8;
        var labelRight = rect.Left + 2 + Math.Max(0, (rect.Width - contentWidth) / 2) + labelWidth;
        var tickLeft = labelRight + 2;
        var barX = tickLeft + tickWidth + 2;

        // Scale: numbers with a tick line beside each, and short minor ticks every 3 dB in between.
        for (var db = 0; db <= (int)range; db += 3)
        {
            var y = (float)Math.Round(Y(-db)) + 0.5f;
            graphics.DrawLine(GridPen, tickLeft + tickWidth - 2, y, tickLeft + tickWidth, y);
        }
        foreach (var mark in new[] { 0, 6, 12, 24, 36, 50 })
        {
            var y = (float)Math.Round(Y(-mark)) + 0.5f;
            graphics.DrawLine(ScaleTickPen, tickLeft, y, tickLeft + tickWidth, y);
            var text = mark.ToString();
            var size = graphics.MeasureString(text, SmallFont);
            graphics.DrawString(text, SmallFont, LabelBrush, labelRight - size.Width, y - size.Height / 2);
        }

        // Left: peak level in dB.
        var peakDb = UpdatePeak(snapshot);
        graphics.FillRectangle(TrackBrush, barX, top, barWidth, bottom - top);
        if (snapshot.HasSignal && peakDb > -range)
        {
            var y = Y(peakDb);
            var warnY = Y(PeakWarningDb);
            graphics.FillRectangle(LevelBrush, barX, Math.Max(y, warnY), barWidth, bottom - Math.Max(y, warnY));
            if (y < warnY) graphics.FillRectangle(VuRedBrush, barX, y, barWidth, warnY - y);
        }
        if (snapshot.HasSignal && _peakHoldDb > -range)
        {
            var holdY = Y(_peakHoldDb);
            graphics.FillRectangle(_peakHoldDb > PeakWarningDb ? VuRedBrush : InkBrush, barX, holdY - 1, barWidth, 2);
        }

        // Right: momentary loudness in LUFS.
        var lufsX = barX + barWidth + gap;
        var lufs = snapshot.HasSignal ? snapshot.MomentaryLufs : -100;
        graphics.FillRectangle(TrackBrush, lufsX, top, barWidth, bottom - top);
        if (snapshot.HasSignal && lufs > -range)
        {
            var y = Y(lufs);
            graphics.FillRectangle(AccentBrush, lufsX, y, barWidth, bottom - y);
        }

        // Readout: the LUFS value, level with the top of the loudness bar (the dB bar speaks for itself).
        var lufsText = lufs > -100 ? $"{lufs:0.0} LUFS" : "-inf LUFS";
        var boxHeight = graphics.MeasureString(lufsText, ValueFont).Height + 2;
        var lufsY = Math.Clamp(Y(lufs) - boxHeight / 2, top, Math.Max(top, bottom - boxHeight - 2));
        DrawValueBox(graphics, lufsText, ValueFont, lufsX + barWidth + 3, lufsY);
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

    /// <summary>Position along the arc (0 = left end, 1 = right end) for a short-term loudness in LUFS.</summary>
    private static double VuPosition(double lufs) => (lufs - VuScaleMin) / (VuScaleMax - VuScaleMin);

    private void DrawVuMeter(Graphics graphics, Rectangle rect, AnalysisSnapshot snapshot)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        // Everything (arc, ticks, labels, needle, readouts) is sized from one gauge height "h", limited by the
        // panel's width as well as its height, so the meter keeps its proportions in tall or narrow panels.
        // In a panel taller than that, the gauge is centred vertically.
        const double halfSpan = 24 * Math.PI / 180;
        var h = Math.Max(24f, Math.Min(rect.Height, rect.Width * 0.52f));
        var gauge = new RectangleF(rect.Left, rect.Top + (rect.Height - h) / 2f, rect.Width, h);
        var halfWidth = Math.Max(20f, Math.Min(rect.Width * 0.38f, h * 1.15f)); // leaves room for the 30 and 0 labels
        var radius = (float)(halfWidth / Math.Sin(halfSpan));
        var centerX = rect.Left + rect.Width / 2f;
        var arcY = gauge.Top + h * 0.38f;
        var pivotY = arcY + radius;

        PointF At(double position, float distance)
        {
            var angle = -halfSpan + position * 2 * halfSpan;
            return new PointF(centerX + (float)Math.Sin(angle) * distance, pivotY - (float)Math.Cos(angle) * distance);
        }
        float GdiAngle(double position) => (float)((-halfSpan + position * 2 * halfSpan) * 180 / Math.PI - 90);

        // Red band from the -9 LUFS reference to 0 LUFS; plain baseline below it.
        var reference = VuPosition(VuReferenceLufs);
        using (var band = new GraphicsPath())
        {
            var outer = radius + h * 0.035f;
            var inner = radius - h * 0.035f;
            var a0 = GdiAngle(reference);
            var a1 = GdiAngle(1);
            band.AddArc(centerX - outer, pivotY - outer, outer * 2, outer * 2, a0, a1 - a0);
            band.AddArc(centerX - inner, pivotY - inner, inner * 2, inner * 2, a1, a0 - a1);
            band.CloseFigure();
            graphics.FillPath(VuRedBrush, band);
        }
        graphics.DrawArc(VuBaselinePen, centerX - radius, pivotY - radius, radius * 2, radius * 2, GdiAngle(0), GdiAngle(reference) - GdiAngle(0));

        // Minor ticks every 1 LU just inside the arc.
        for (var lufs = (int)VuScaleMin; lufs <= (int)VuScaleMax; lufs++)
        {
            var position = VuPosition(lufs);
            var minorPen = lufs > VuReferenceLufs ? VuRedMinorPen : VuMinorPen;
            graphics.DrawLine(minorPen, At(position, radius - h * 0.02f), At(position, radius - h * (lufs % 3 == 0 ? 0.075f : 0.05f)));
        }

        var fontPx = Math.Clamp(h * 0.12f, 9f, 20f);
        using var scaleFont = new Font(UiFonts.Numbers, fontPx, FontStyle.Regular, GraphicsUnit.Pixel);
        using var unitFont = new Font(UiFonts.Small, fontPx * 0.62f, FontStyle.Bold, GraphicsUnit.Pixel);
        foreach (var mark in VuMajorMarks)
        {
            var position = VuPosition(mark);
            var red = mark > VuReferenceLufs;
            graphics.DrawLine(red ? VuRedPen : VuInkPen, At(position, radius), At(position, radius + h * 0.13f));
            var label = Math.Abs(mark).ToString();
            var point = At(position, radius + h * 0.23f);
            var size = graphics.MeasureString(label, scaleFont);
            graphics.DrawString(label, scaleFont, red ? VuRedBrush : InkBrush, point.X - size.Width / 2, point.Y - size.Height / 2);
        }

        // Unit caption under the middle of the arc, so the scale is unambiguous.
        const string unit = "−LUFS-S";
        var unitPoint = At(0.5, radius - h * 0.16f);
        var unitSize = graphics.MeasureString(unit, unitFont);
        graphics.DrawString(unit, unitFont, LabelBrush, unitPoint.X - unitSize.Width / 2, unitPoint.Y - unitSize.Height / 2);

        // Readouts: short-term loudness on the left, distance from the 0 VU reference on the right.
        var shortTerm = snapshot.HasSignal ? snapshot.ShortTermLufs : -100;
        var hasLoudness = shortTerm > -100;
        var leftText = hasLoudness ? $"{shortTerm:0.0} LUFS-S" : "-inf LUFS-S";
        var rightText = hasLoudness ? $"{shortTerm - VuReferenceLufs:+0.0;-0.0} LU" : "-- LU";
        var boxY = gauge.Bottom - ValueFont.Height - 6f;
        DrawValueBox(graphics, leftText, ValueFont, rect.Left + 6, boxY);
        DrawValueBox(graphics, rightText, ValueFont, rect.Right - 6, boxY, true);

        // Needle: a classic VU movement. It is driven by fast (25 ms) K-weighted loudness and has mass and damping
        // like the real thing (settles in ~300 ms with ~1.5 % overshoot), so it swings with the beat. It works on
        // signal amplitude, as a moving coil does, then is shown on the LUFS scale; a steady sound settles exactly
        // on its LUFS value. The readout boxes still show short-term LUFS.
        var drive = snapshot.HasSignal && snapshot.FastLufs > -100 ? Math.Pow(10, (snapshot.FastLufs + 0.691) / 20) : 0;
        var now = DateTime.UtcNow;
        var elapsed = _lastNeedleUpdate == DateTime.MinValue ? 0.016 : Math.Min(0.25, (now - _lastNeedleUpdate).TotalSeconds);
        _lastNeedleUpdate = now;
        const double omega = 18.9, damping = 0.81; // natural frequency (rad/s) and damping ratio of a VU movement
        for (var remaining = elapsed; remaining > 0; remaining -= 0.002)
        {
            var dt = Math.Min(0.002, remaining);
            var acceleration = omega * omega * (drive - _needleAmplitude) - 2 * damping * omega * _needleVelocity;
            _needleVelocity += acceleration * dt;
            _needleAmplitude += _needleVelocity * dt;
            if (_needleAmplitude < 0) { _needleAmplitude = 0; _needleVelocity = 0; } // resting against the left stop
        }
        _needleVu = _needleAmplitude > 1e-6 ? -0.691 + 20 * Math.Log10(_needleAmplitude) : VuScaleMin - 6;
        var needle = Math.Clamp(VuPosition(_needleVu), -0.03, 1.03);
        var state = graphics.Save();
        graphics.SetClip(gauge); // the needle's pivot sits below the gauge, as on a real meter
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

    /// <summary>Spectrogram colour table (quiet to loud) from the active theme's colour ramp.</summary>
    private static int[] BuildInfernoLut()
    {
        var stops = Palette.Theme.Spectrogram;
        var lut = new int[256];
        for (var i = 0; i < lut.Length; i++)
        {
            var scaled = i / 255.0 * (stops.Length - 1);
            var index = Math.Min(stops.Length - 2, (int)scaled);
            lut[i] = Palette.Mix(stops[index], stops[index + 1], scaled - index).ToArgb();
        }
        return lut;
    }
}
