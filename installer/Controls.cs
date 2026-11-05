using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace Spectrm.Setup
{
    /// <summary>SPECTRM palette (same as the app): mint, violet and hot pink on teal-tinted black.</summary>
    internal static class Theme
    {
        public static readonly Color Background = Color.FromArgb(3, 7, 7);
        public static readonly Color Accent = Color.FromArgb(112, 240, 200);
        public static readonly Color Secondary = Color.FromArgb(150, 132, 255);
        public static readonly Color Highlight = Color.FromArgb(255, 84, 140);
        public static readonly Color Ink = Color.FromArgb(228, 240, 236);
        public static readonly Color Label = Color.FromArgb(118, 146, 142);
        public static readonly Color Track = Color.FromArgb(10, 22, 21);
        public static readonly Color Box = Color.FromArgb(15, 38, 36);
        public static readonly Color Line = Color.FromArgb(30, 54, 51);
        public static readonly Color Hover = Color.FromArgb(18, 36, 34);
        public static readonly Color CloseHover = Color.FromArgb(196, 43, 28);

        private static readonly string[] Installed = new InstalledFontCollection().Families.Select(f => f.Name).ToArray();

        private static string Pick(string preferred, string fallback) =>
            Installed.Any(name => string.Equals(name, preferred, StringComparison.OrdinalIgnoreCase)) ? preferred : fallback;

        public static readonly string Text = Pick("Segoe UI Variable Text", "Segoe UI");
        public static readonly string Display = Pick("Segoe UI Variable Display", "Segoe UI");
        public static readonly string Small = Pick("Segoe UI Variable Small", "Segoe UI");

        public static GraphicsPath Rounded(RectangleF rect, float radius) => LogoArt.RoundedRectangle(rect, radius);

        public static Color Mix(Color a, Color b, float t) => Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    /// <summary>Flat rounded button: mint primary, pink danger, outlined secondary, or a bare title-bar glyph.</summary>
    internal sealed class FlatButton : Control
    {
        public enum Kind { Primary, Danger, Secondary, TitleBar }

        private bool _hover;
        private bool _down;
        public Kind Style = Kind.Secondary;

        public FlatButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleRole = AccessibleRole.PushButton;
        }

        public void PerformClick()
        {
            if (Enabled && Visible) OnClick(EventArgs.Empty);
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            AccessibleName = Text;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _down = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _down = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _down = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.KeyCode == Keys.Space) PerformClick();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Background);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            var radius = Math.Min(Height / 2f, 7f * DeviceScale);
            Color text;

            switch (Style)
            {
                case Kind.Primary:
                case Kind.Danger:
                {
                    var baseColor = Style == Kind.Primary ? Theme.Accent : Theme.Highlight;
                    var fill = !Enabled ? Theme.Mix(baseColor, Theme.Background, 0.6f)
                        : _down ? Theme.Mix(baseColor, Color.Black, 0.18f)
                        : _hover ? Theme.Mix(baseColor, Color.White, 0.18f)
                        : baseColor;
                    using (var path = Theme.Rounded(rect, radius))
                    using (var brush = new SolidBrush(fill))
                        g.FillPath(brush, path);
                    text = Color.FromArgb(4, 12, 11);
                    break;
                }
                case Kind.TitleBar:
                {
                    if (_hover && Enabled)
                        using (var brush = new SolidBrush(_down ? Theme.Mix(Theme.CloseHover, Color.Black, 0.2f) : Theme.CloseHover))
                            g.FillRectangle(brush, ClientRectangle);
                    text = Enabled ? Theme.Ink : Theme.Line;
                    break;
                }
                default:
                {
                    using (var path = Theme.Rounded(rect, radius))
                    {
                        if (_hover || _down)
                            using (var brush = new SolidBrush(_down ? Theme.Line : Theme.Hover))
                                g.FillPath(brush, path);
                        using (var pen = new Pen(_hover ? Theme.Mix(Theme.Line, Theme.Accent, 0.35f) : Theme.Line))
                            g.DrawPath(pen, path);
                    }
                    text = Enabled ? Theme.Ink : Theme.Label;
                    break;
                }
            }

            if (Focused && ShowFocusCues && Style != Kind.TitleBar)
                using (var path = Theme.Rounded(RectangleF.Inflate(rect, -2.5f, -2.5f), Math.Max(1f, radius - 2.5f)))
                using (var pen = new Pen(Style == Kind.Secondary ? Theme.Accent : Color.FromArgb(4, 12, 11), 1.5f))
                    g.DrawPath(pen, path);

            TextRenderer.DrawText(g, Text, Font, ClientRectangle, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        private float DeviceScale
        {
            get { using (var g = CreateGraphics()) return g.DpiX / 96f; }
        }
    }

    /// <summary>A labelled on/off switch.</summary>
    internal sealed class ToggleRow : Control
    {
        private bool _checked;
        private bool _hover;
        public float DpiScale = 1f;

        public ToggleRow()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleRole = AccessibleRole.CheckButton;
        }

        public bool Checked
        {
            get => _checked;
            set
            {
                if (_checked == value) return;
                _checked = value;
                AccessibleDescription = value ? "On" : "Off";
                Invalidate();
            }
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            AccessibleName = Text;
            Invalidate();
        }

        protected override void OnClick(EventArgs e)
        {
            Checked = !Checked;
            base.OnClick(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.KeyCode == Keys.Space) OnClick(EventArgs.Empty);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Background);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float w = 34 * DpiScale, h = 18 * DpiScale;
            var track = new RectangleF(0.5f, (Height - h) / 2f, w, h);
            using (var path = Theme.Rounded(track, h / 2f))
            {
                using (var brush = new SolidBrush(_checked ? Theme.Accent : Theme.Track))
                    g.FillPath(brush, path);
                if (!_checked)
                    using (var pen = new Pen(_hover ? Theme.Mix(Theme.Line, Theme.Accent, 0.4f) : Theme.Line, 1f))
                        g.DrawPath(pen, path);
            }
            var knob = h - 6 * DpiScale;
            var knobX = _checked ? track.Right - 3 * DpiScale - knob : track.X + 3 * DpiScale;
            using (var brush = new SolidBrush(_checked ? Color.FromArgb(4, 12, 11) : Theme.Label))
                g.FillEllipse(brush, knobX, track.Y + 3 * DpiScale, knob, knob);

            if (Focused && ShowFocusCues)
                using (var path = Theme.Rounded(RectangleF.Inflate(track, 2.5f, 2.5f), h / 2f + 2.5f))
                using (var pen = new Pen(Theme.Secondary, 1.5f))
                    g.DrawPath(pen, path);

            var textRect = new Rectangle((int)(w + 12 * DpiScale), 0, Width - (int)(w + 12 * DpiScale), Height);
            TextRenderer.DrawText(g, Text, Font, textRect, _hover ? Color.White : Theme.Ink,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
    }
}
