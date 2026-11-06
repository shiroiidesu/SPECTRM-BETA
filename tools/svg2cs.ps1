# SPECTRM - real-time audio meter for Windows
# Copyright (C) 2026 shiroiidesu
# SPDX-License-Identifier: GPL-3.0-or-later
#
# This program is free software: you can redistribute it and/or modify it under the terms of the GNU General
# Public License as published by the Free Software Foundation, either version 3 of the License, or (at your
# option) any later version. It is distributed WITHOUT ANY WARRANTY; see the LICENSE file for details.
param([string]$Svg, [string]$Out)
# Extracts the SPECTRM logo SVG's path data into a C# source file (LogoData.cs).
$text = Get-Content -Raw -LiteralPath $Svg
$body = $text.Substring($text.IndexOf('</defs>'))
$split = $body.IndexOf('matrix(1, 0, 0, 1, 72, 333)')
$barsPart = $body.Substring(0, $split)
$glyphPart = $body.Substring($split)
$bars = [regex]::Matches($barsPart, ' d="([^"]+)"') | ForEach-Object { $_.Groups[1].Value.Trim() }
$glyphs = [regex]::Matches($glyphPart, 'translate\(([-\d.]+), ([-\d.]+)\)"><g><path d="([^"]+)"') | ForEach-Object {
    [pscustomobject]@{ X = [double]$_.Groups[1].Value + 72; Y = [double]$_.Groups[2].Value + 333; D = $_.Groups[3].Value.Trim() }
}
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('// SPECTRM - real-time audio meter for Windows')
[void]$sb.AppendLine('// Copyright (C) 2026 shiroiidesu')
[void]$sb.AppendLine('// SPDX-License-Identifier: GPL-3.0-or-later')
[void]$sb.AppendLine('//')
[void]$sb.AppendLine('// This program is free software: you can redistribute it and/or modify it under the terms of the GNU General')
[void]$sb.AppendLine('// Public License as published by the Free Software Foundation, either version 3 of the License, or (at your')
[void]$sb.AppendLine('// option) any later version. It is distributed WITHOUT ANY WARRANTY; see the LICENSE file for details.')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('// Generated from "SPECTRM logo.svg" (viewBox 0 0 768 768). Path data uses absolute M/L/C/Z commands only.')
[void]$sb.AppendLine('namespace Spectrm')
[void]$sb.AppendLine('{')
[void]$sb.AppendLine('    internal static class LogoData')
[void]$sb.AppendLine('    {')
[void]$sb.AppendLine('        /// <summary>The seven rounded meter bars of the mark.</summary>')
[void]$sb.AppendLine('        public static readonly string[] Bars =')
[void]$sb.AppendLine('        {')
foreach ($b in $bars) { [void]$sb.AppendLine('            "' + $b + '",') }
[void]$sb.AppendLine('        };')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('        /// <summary>Wordmark letters S P E C T R M; each is offset by GlyphOffsets.</summary>')
[void]$sb.AppendLine('        public static readonly string[] Glyphs =')
[void]$sb.AppendLine('        {')
foreach ($g in $glyphs) { [void]$sb.AppendLine('            "' + $g.D + '",') }
[void]$sb.AppendLine('        };')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('        public static readonly float[] GlyphOffsets =')
[void]$sb.AppendLine('        {')
foreach ($g in $glyphs) { [void]$sb.AppendLine(('            {0}f, {1}f,' -f $g.X.ToString([cultureinfo]::InvariantCulture), $g.Y.ToString([cultureinfo]::InvariantCulture))) }
[void]$sb.AppendLine('        };')
[void]$sb.AppendLine('    }')
[void]$sb.AppendLine('}')
[System.IO.File]::WriteAllText($Out, $sb.ToString())
"bars=$($bars.Count) glyphs=$($glyphs.Count)"
