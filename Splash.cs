// SPECTRM - real-time audio meter for Windows
// Copyright (C) 2026 shiroiidesu
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This program is free software: you can redistribute it and/or modify it under the terms of the GNU General
// Public License as published by the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version. It is distributed WITHOUT ANY WARRANTY; see the LICENSE file for details.
using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace Spectrm;

/// <summary>
/// Startup animation over the meters: the SPECTRM letters fade in left to right while the bars grow
/// out of dots from the centre, the logo holds, fades out, and the cover opens from the middle to reveal the meters.
/// Any click or key skips ahead.
/// </summary>
internal sealed class StartupSplash : Control
{
    // Timeline, in milliseconds from when the window is first shown.
    private const int LettersStart = 120, LetterStagger = 55, LetterDuration = 520;
    private const int BarsStart = 420, BarStagger = 70, BarDuration = 520;
    private const int FadeOutStart = 1550, FadeOutDuration = 260;
    private const int RevealStart = FadeOutStart + FadeOutDuration, RevealDuration = 340;
    private const int End = RevealStart + RevealDuration;
    private const float LetterRise = 0.12f; // fraction of the logo height each letter rises as it fades in

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    private readonly Stopwatch _clock = new();
    private readonly RectangleF _logoBounds;
    private long _skipOffset;

    public event EventHandler? Finished;

    public StartupSplash()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Black;
        Cursor = Cursors.Default;
        using (var full = LogoArt.CreateFull()) _logoBounds = full.GetBounds();
        _timer.Tick += (_, _) => Step();
    }

    public bool Running => _clock.IsRunning;

    public void Start()
    {
        _clock.Restart();
        _timer.Start();
    }

    /// <summary>Jumps straight to the fade-out.</summary>
    public void Skip()
    {
        var now = Elapsed;
        if (now < FadeOutStart) _skipOffset += FadeOutStart - now;
    }

    private long Elapsed => _clock.ElapsedMilliseconds + _skipOffset;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Skip();
    }

    protected override void WndProc(ref Message m)
    {
        if (EdgePassThrough.Handle(this, ref m)) return;
        base.WndProc(ref m);
    }

    private void Step()
    {
        var t = Elapsed;
        if (t >= End)
        {
            _timer.Stop();
            _clock.Stop();
            Finished?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (t >= RevealStart)
        {
            // Open the cover from the centre along the window's long side, uncovering the live meters.
            var p = EaseInOutCubic((t - RevealStart) / (float)RevealDuration);
            var horizontal = Width >= Height;
            var length = horizontal ? Width : Height;
            var gap = (int)Math.Round(length * p);
            var first = (length - gap) / 2;
            var region = new Region(horizontal ? new Rectangle(0, 0, first, Height) : new Rectangle(0, 0, Width, first));
            region.Union(horizontal ? new Rectangle(first + gap, 0, Width - first - gap, Height) : new Rectangle(0, first + gap, Width, Height - first - gap));
            var old = Region;
            Region = region;
            old?.Dispose();
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.Black);
        if (Width <= 0 || Height <= 0) return;
        var t = Elapsed;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        if (t >= RevealStart)
        {
            PaintRevealEdges(g, (t - RevealStart) / (float)RevealDuration);
            return;
        }

        var fade = 1f - Clamp01((t - FadeOutStart) / (float)FadeOutDuration);

        // Fit the logo: about a third of the window's height, never more than 70% of its width.
        var aspect = _logoBounds.Width / _logoBounds.Height;
        var height = Math.Min(Math.Min(Height * 0.34f, Width * 0.7f / aspect), 72f);
        var scale = height / _logoBounds.Height;
        var left = (Width - _logoBounds.Width * scale) / 2f;
        var top = (Height - height) / 2f;

        for (var i = 0; i < LogoArt.GlyphCount; i++)
        {
            var p = EaseOutCubic(Clamp01((t - LettersStart - i * LetterStagger) / (float)LetterDuration));
            if (p <= 0f) continue;
            using var glyph = LogoArt.CreateGlyph(i);
            using var matrix = Placement(left, top + (1f - p) * height * LetterRise, scale);
            glyph.Transform(matrix);
            using var brush = new SolidBrush(Color.FromArgb((int)(255 * p * fade), Palette.Ink));
            g.FillPath(brush, glyph);
        }

        var centre = (LogoArt.BarCount - 1) / 2;
        for (var i = 0; i < LogoArt.BarCount; i++)
        {
            var p = Clamp01((t - BarsStart - Math.Abs(i - centre) * BarStagger) / (float)BarDuration);
            if (p <= 0f) continue;
            using var bar = LogoArt.CreateBar(i, EaseOutBack(p));
            using var matrix = Placement(left, top, scale);
            bar.Transform(matrix);
            var alpha = Math.Min(1f, p * 2.5f) * fade;
            using var brush = new SolidBrush(Color.FromArgb((int)(255 * alpha), Palette.Ink));
            g.FillPath(brush, bar);
        }
    }

    /// <summary>A thin mint line on each edge of the opening, fading as it opens.</summary>
    private void PaintRevealEdges(Graphics g, float progress)
    {
        var horizontal = Width >= Height;
        var length = horizontal ? Width : Height;
        var gap = (int)Math.Round(length * EaseInOutCubic(progress));
        var first = (length - gap) / 2;
        using var pen = new Pen(Color.FromArgb((int)(200 * (1f - progress)), Palette.Accent), 2f);
        if (horizontal)
        {
            g.DrawLine(pen, first - 1, 0, first - 1, Height);
            g.DrawLine(pen, first + gap + 1, 0, first + gap + 1, Height);
        }
        else
        {
            g.DrawLine(pen, 0, first - 1, Width, first - 1);
            g.DrawLine(pen, 0, first + gap + 1, Width, first + gap + 1);
        }
    }

    private Matrix Placement(float left, float top, float scale)
    {
        var matrix = new Matrix();
        matrix.Translate(left, top);
        matrix.Scale(scale, scale);
        matrix.Translate(-_logoBounds.X, -_logoBounds.Y);
        return matrix;
    }

    private static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;
    private static float EaseOutCubic(float x) => 1f - MathF.Pow(1f - x, 3f);
    private static float EaseInOutCubic(float x) => x < 0.5f ? 4f * x * x * x : 1f - MathF.Pow(-2f * x + 2f, 3f) / 2f;

    /// <summary>Overshoots slightly and settles, like a meter needle.</summary>
    private static float EaseOutBack(float x)
    {
        const float c1 = 1.70158f, c3 = c1 + 1f;
        return 1f + c3 * MathF.Pow(x - 1f, 3f) + c1 * MathF.Pow(x - 1f, 2f);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            Region?.Dispose();
        }
        base.Dispose(disposing);
    }
}
