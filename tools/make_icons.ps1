# SPECTRM - real-time audio meter for Windows
# Copyright (C) 2026 shiroiidesu
# SPDX-License-Identifier: GPL-3.0-or-later
#
# This program is free software: you can redistribute it and/or modify it under the terms of the GNU General
# Public License as published by the Free Software Foundation, either version 3 of the License, or (at your
# option) any later version. It is distributed WITHOUT ANY WARRANTY; see the LICENSE file for details.
param([string]$Project)
# Renders SPECTRM.ico and the branding PNGs from Logo.cs + LogoData.cs.
$logo = Get-Content -Raw -LiteralPath (Join-Path $Project 'Logo.cs')
$data = Get-Content -Raw -LiteralPath (Join-Path $Project 'LogoData.cs')
$gen = @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
namespace Spectrm
{
    public static class IconMaker
    {
        public static void SavePng(Bitmap bitmap, string path) { bitmap.Save(path, ImageFormat.Png); }

        public static void Png(int size, string path) { using (var b = LogoArt.RenderIcon(size)) b.Save(path, ImageFormat.Png); }

        public static void Logo(int height, int r, int g, int b, string path)
        {
            using (var bmp = LogoArt.RenderLogo(height, Color.FromArgb(r, g, b))) bmp.Save(path, ImageFormat.Png);
        }

        public static void Ico(int[] sizes, string path)
        {
            var images = new List<byte[]>();
            foreach (var size in sizes)
                using (var bmp = LogoArt.RenderIcon(size))
                    images.Add(size >= 256 ? PngBytes(bmp) : DibBytes(bmp));
            using (var stream = File.Create(path))
            using (var w = new BinaryWriter(stream))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32);
                    w.Write(images[i].Length); w.Write(offset);
                    offset += images[i].Length;
                }
                foreach (var image in images) w.Write(image);
            }
        }

        private static byte[] PngBytes(Bitmap bmp)
        {
            using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); return ms.ToArray(); }
        }

        private static byte[] DibBytes(Bitmap bmp)
        {
            int n = bmp.Width;
            int maskStride = ((n + 31) / 32) * 4;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(40); w.Write(n); w.Write(n * 2); w.Write((short)1); w.Write((short)32);
                w.Write(0); w.Write(n * n * 4 + maskStride * n); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
                var mask = new byte[maskStride * n];
                for (int y = n - 1; y >= 0; y--)
                {
                    int row = n - 1 - y;
                    for (int x = 0; x < n; x++)
                    {
                        var c = bmp.GetPixel(x, y);
                        w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
                        if (c.A == 0) mask[row * maskStride + x / 8] |= (byte)(0x80 >> (x % 8));
                    }
                }
                w.Write(mask);
                return ms.ToArray();
            }
        }
    }
}
'@
# One compilation unit: hoist every using directive to the top.
$all = ($logo + "`n" + $data + "`n" + $gen) -split "`r?`n"
$usings = $all | Where-Object { $_ -match '^using [\w.]+;\s*$' } | Sort-Object -Unique
$rest = $all | Where-Object { $_ -notmatch '^using [\w.]+;\s*$' }
Add-Type -TypeDefinition (($usings + $rest) -join "`n") -ReferencedAssemblies System.Drawing -Language CSharp
$brand = Join-Path $Project 'branding'
New-Item -ItemType Directory -Force $brand | Out-Null
$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 256
[Spectrm.IconMaker]::Ico([int[]]$sizes, (Join-Path $Project 'SPECTRM.ico'))
foreach ($s in ($sizes + 512, 1024)) { [Spectrm.IconMaker]::Png($s, (Join-Path $brand "icon-$s.png")) }
[Spectrm.IconMaker]::Logo(300, 255, 255, 255, (Join-Path $brand 'logo-white.png'))
[Spectrm.IconMaker]::Logo(300, 10, 22, 21, (Join-Path $brand 'logo-dark.png'))
Get-ChildItem $brand, (Join-Path $Project 'SPECTRM.ico') | Select-Object Name, Length
