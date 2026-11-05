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
