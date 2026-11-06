// SPECTRM - real-time audio meter for Windows
// Copyright (C) 2026 shiroiidesu
// SPDX-License-Identifier: GPL-3.0-or-later
//
// This program is free software: you can redistribute it and/or modify it under the terms of the GNU General
// Public License as published by the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version. It is distributed WITHOUT ANY WARRANTY; see the LICENSE file for details.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;

// Kept to C# 5 syntax so the icon generator script (Windows PowerShell Add-Type) can compile it too.
namespace Spectrm
{
    /// <summary>
    /// The SPECTRM logo from "SPECTRM logo.svg", as vector paths: the wordmark plus the seven-bar mark.
    /// Used for the top-bar logo and to render the app icon at every size.
    /// </summary>
    internal static class LogoArt
    {
        public static readonly Color MarkColor = Color.FromArgb(240, 248, 245);
        public static readonly Color TileTop = Color.FromArgb(16, 34, 32);
        public static readonly Color TileBottom = Color.FromArgb(3, 9, 9);
        public static readonly Color TileEdge = Color.FromArgb(38, 66, 62);

        /// <summary>Bar heights relative to the centre bar, left to right (the mark is symmetric).</summary>
        private static readonly float[] BarRatios = { 1f / 3f, 2f / 3f, 4f / 9f, 1f, 4f / 9f, 2f / 3f, 1f / 3f };

        /// <summary>The seven-bar mark, in SVG units.</summary>
        public static GraphicsPath CreateMark()
        {
            var path = new GraphicsPath(FillMode.Winding);
            foreach (var data in LogoData.Bars) AddSvgPath(path, data, 0f, 0f);
            return path;
        }

        /// <summary>The "SPECTRM" wordmark, in SVG units.</summary>
        public static GraphicsPath CreateWordmark()
        {
            var path = new GraphicsPath(FillMode.Winding);
            for (int i = 0; i < LogoData.Glyphs.Length; i++)
                AddSvgPath(path, LogoData.Glyphs[i], LogoData.GlyphOffsets[i * 2], LogoData.GlyphOffsets[i * 2 + 1]);
            return path;
        }

        public static int GlyphCount { get { return LogoData.Glyphs.Length; } }

        public static int BarCount { get { return BarRatios.Length; } }

        /// <summary>One letter of the wordmark (0 = S … 6 = M), in SVG units.</summary>
        public static GraphicsPath CreateGlyph(int index)
        {
            var path = new GraphicsPath(FillMode.Winding);
            AddSvgPath(path, LogoData.Glyphs[index], LogoData.GlyphOffsets[index * 2], LogoData.GlyphOffsets[index * 2 + 1]);
            return path;
        }

        private static RectangleF _markBounds;

        /// <summary>
        /// One bar of the mark (0 = leftmost) as a pill, in SVG units. <paramref name="heightScale"/> 1 is its
        /// real height; 0 shrinks it to a dot, which is how the startup animation grows the bars.
        /// </summary>
        public static GraphicsPath CreateBar(int index, float heightScale)
        {
            if (_markBounds.IsEmpty)
                using (var mark = CreateMark()) _markBounds = mark.GetBounds();
            var m = _markBounds;
            float width = m.Width / (BarRatios.Length * 1.5f - 0.5f); // bars are twice as wide as the gaps
            float height = Math.Max(width, m.Height * BarRatios[index] * heightScale);
            float centreY = m.Y + m.Height / 2f;
            return RoundedRectangle(new RectangleF(m.X + index * width * 1.5f, centreY - height / 2f, width, height), width / 2f);
        }

        /// <summary>Wordmark and mark together, as laid out in the SVG.</summary>
        public static GraphicsPath CreateFull()
        {
            var path = CreateWordmark();
            using (var mark = CreateMark()) path.AddPath(mark, false);
            return path;
        }

        /// <summary>Scales and moves <paramref name="path"/> in place so it fits <paramref name="target"/>, centred, keeping its proportions.</summary>
        public static void FitInto(GraphicsPath path, RectangleF target)
        {
            var bounds = path.GetBounds();
            float scale = Math.Min(target.Width / bounds.Width, target.Height / bounds.Height);
            using (var matrix = new Matrix())
            {
                matrix.Translate(target.X + (target.Width - bounds.Width * scale) / 2f, target.Y + (target.Height - bounds.Height * scale) / 2f);
                matrix.Scale(scale, scale);
                matrix.Translate(-bounds.X, -bounds.Y);
                path.Transform(matrix);
            }
        }

        /// <summary>Full logo on a transparent background, <paramref name="height"/> pixels tall.</summary>
        public static Bitmap RenderLogo(int height, Color color)
        {
            using (var path = CreateFull())
            {
                var bounds = path.GetBounds();
                int pad = Math.Max(2, height / 10);
                int width = (int)Math.Ceiling(bounds.Width * (height - 2 * pad) / bounds.Height) + 2 * pad;
                var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using (var graphics = Graphics.FromImage(bitmap))
                using (var brush = new SolidBrush(color))
                {
                    graphics.Clear(Color.Transparent);
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    FitInto(path, new RectangleF(pad, pad, width - 2 * pad, height - 2 * pad));
                    graphics.FillPath(brush, path);
                }
                return bitmap;
            }
        }

        /// <summary>
        /// App icon: the mark in white on a dark rounded tile. Sizes up to 48 px snap the bars to whole pixels
        /// so they stay crisp in the title bar, taskbar and Explorer; larger sizes use the vector mark.
        /// </summary>
        public static Bitmap RenderIcon(int size)
        {
            var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;

                float inset = size >= 64 ? size * 0.035f : 0f;
                var tile = new RectangleF(inset, inset, size - 2 * inset, size - 2 * inset);
                using (var tilePath = RoundedRectangle(tile, tile.Width * 0.22f))
                using (var fill = new LinearGradientBrush(new RectangleF(tile.X, tile.Y - 1, tile.Width, tile.Height + 2), TileTop, TileBottom, 90f))
                {
                    graphics.FillPath(fill, tilePath);
                    if (size >= 32)
                    {
                        float edge = Math.Max(1f, size / 96f);
                        var edgeRect = RectangleF.Inflate(tile, -edge / 2f, -edge / 2f);
                        using (var edgePath = RoundedRectangle(edgeRect, edgeRect.Width * 0.22f))
                        using (var pen = new Pen(TileEdge, edge))
                            graphics.DrawPath(pen, edgePath);
                    }
                }

                using (var brush = new SolidBrush(MarkColor))
                {
                    int barWidth, gap, height;
                    if (PixelGrid(size, out barWidth, out gap, out height))
                        DrawSnappedMark(graphics, brush, size, barWidth, gap, height);
                    else
                        using (var mark = CreateMark())
                        {
                            float markWidth = tile.Width * 0.6f;
                            FitInto(mark, new RectangleF(tile.X + (tile.Width - markWidth) / 2f, tile.Y + tile.Height * 0.2f, markWidth, tile.Height * 0.6f));
                            graphics.FillPath(brush, mark);
                        }
                }
            }
            return bitmap;
        }

        /// <summary>Hand-picked whole-pixel bar width, gap and centre-bar height for the small icon sizes.</summary>
        private static bool PixelGrid(int size, out int barWidth, out int gap, out int height)
        {
            barWidth = 0; gap = 0; height = 0;
            if (size <= 20) { barWidth = 1; gap = 1; height = 12; }
            else if (size <= 24) { barWidth = 2; gap = 1; height = 16; }
            else if (size <= 32) { barWidth = 2; gap = 1; height = 18; }
            else if (size <= 40) { barWidth = 3; gap = 1; height = 24; }
            else if (size <= 48) { barWidth = 3; gap = 2; height = 30; }
            else return false;
            height = Math.Min(height, size - 4);
            return true;
        }

        private static void DrawSnappedMark(Graphics graphics, Brush brush, int size, int barWidth, int gap, int height)
        {
            DrawPixelMark(graphics, brush, (size - MarkWidth(barWidth, gap)) / 2, 0, size, barWidth, gap, height);
        }

        public static int MarkWidth(int barWidth, int gap)
        {
            return BarRatios.Length * barWidth + (BarRatios.Length - 1) * gap;
        }

        /// <summary>The mark on whole pixels, bars centred vertically in a <paramref name="boxHeight"/>-tall band starting at <paramref name="top"/>.</summary>
        public static void DrawPixelMark(Graphics graphics, Brush brush, int left, int top, int boxHeight, int barWidth, int gap, int height)
        {
            int size = boxHeight;
            var oldMode = graphics.SmoothingMode;
            graphics.SmoothingMode = barWidth >= 3 ? SmoothingMode.AntiAlias : SmoothingMode.None;
            for (int i = 0; i < BarRatios.Length; i++)
            {
                // Keep each bar's height the same parity as the tile so every bar is centred on whole pixels.
                int barHeight = (int)Math.Round(height * BarRatios[i]);
                if ((barHeight - size) % 2 != 0) barHeight += (height * BarRatios[i] > barHeight) ? 1 : -1;
                var rect = new RectangleF(left + i * (barWidth + gap), top + (size - barHeight) / 2, barWidth, barHeight);
                if (barWidth >= 3)
                    using (var bar = RoundedRectangle(rect, barWidth / 2f)) graphics.FillPath(brush, bar);
                else
                    graphics.FillRectangle(brush, rect);
            }
            graphics.SmoothingMode = oldMode;
        }

        public static GraphicsPath RoundedRectangle(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2f, Math.Min(rect.Width, rect.Height));
            if (d <= 0.01f) { path.AddRectangle(rect); return path; }
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>Minimal SVG path reader for absolute M, L, C and Z commands (all the logo uses).</summary>
        private static void AddSvgPath(GraphicsPath path, string data, float dx, float dy)
        {
            var tokens = data.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            var current = PointF.Empty;
            var start = PointF.Empty;
            char command = 'M';
            int i = 0;
            while (i < tokens.Length)
            {
                if (char.IsLetter(tokens[i][0])) command = tokens[i++][0];
                switch (command)
                {
                    case 'M':
                        path.StartFigure();
                        current = start = Read(tokens, ref i, dx, dy);
                        command = 'L'; // further coordinate pairs after M are line-tos
                        break;
                    case 'L':
                        var to = Read(tokens, ref i, dx, dy);
                        path.AddLine(current, to);
                        current = to;
                        break;
                    case 'C':
                        var c1 = Read(tokens, ref i, dx, dy);
                        var c2 = Read(tokens, ref i, dx, dy);
                        var end = Read(tokens, ref i, dx, dy);
                        path.AddBezier(current, c1, c2, end);
                        current = end;
                        break;
                    case 'Z':
                    case 'z':
                        path.CloseFigure();
                        current = start;
                        break;
                    default:
                        throw new FormatException("Unsupported SVG path command: " + command);
                }
            }
        }

        private static PointF Read(string[] tokens, ref int i, float dx, float dy)
        {
            float x = float.Parse(tokens[i++], CultureInfo.InvariantCulture);
            float y = float.Parse(tokens[i++], CultureInfo.InvariantCulture);
            return new PointF(x + dx, y + dy);
        }
    }
}
