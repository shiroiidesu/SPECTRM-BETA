# SPECTRM - real-time audio meter for Windows
# Copyright (C) 2026 shiroiidesu
# SPDX-License-Identifier: GPL-3.0-or-later
#
# This program is free software: you can redistribute it and/or modify it under the terms of the GNU General
# Public License as published by the Free Software Foundation, either version 3 of the License, or (at your
# option) any later version. It is distributed WITHOUT ANY WARRANTY; see the LICENSE file for details.
# Builds the single-file SPECTRM installer: dist\SPECTRM-Setup-<version>.exe
#
#   powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1
#
# 1. Publishes SPECTRM.exe (self-contained, single file).
# 2. Builds the setup program (installer\SpectrmSetup.csproj, .NET Framework 4.8 - built into Windows).
# 3. Appends the gzip-compressed SPECTRM.exe plus a small footer to the setup program.
# The version comes from <Version> in MeteringTool.csproj.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path $root 'obj\installer-build'

Write-Host '== Publishing SPECTRM.exe' -ForegroundColor Cyan
# Published into the build folder (not publish\) so a running copy of SPECTRM doesn't block the build.
$appDir = Join-Path $work 'app'
dotnet publish (Join-Path $root 'MeteringTool.csproj') -c Release -o $appDir --nologo | Out-Host
if ($LASTEXITCODE) { throw 'Publishing SPECTRM failed.' }
$appExe = Join-Path $appDir 'SPECTRM.exe'
$version = ((Get-Item $appExe).VersionInfo.ProductVersion -split '\+')[0]

Write-Host "== Building the setup program (version $version)" -ForegroundColor Cyan
$stubDir = Join-Path $work 'setup'
dotnet build (Join-Path $root 'installer\SpectrmSetup.csproj') -c Release -o $stubDir "-p:Version=$version" --nologo | Out-Host
if ($LASTEXITCODE) { throw 'Building the setup program failed.' }
$stub = Join-Path $stubDir 'SPECTRM-Setup.exe'

Write-Host '== Packing SPECTRM.exe into the installer' -ForegroundColor Cyan
Add-Type -AssemblyName System.IO.Compression
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$out = Join-Path $dist "SPECTRM-Setup-$version.exe"
$stream = [IO.File]::Create($out)
try {
    $stubBytes = [IO.File]::ReadAllBytes($stub)
    $stream.Write($stubBytes, 0, $stubBytes.Length)
    $start = $stream.Position
    $gzip = New-Object IO.Compression.GZipStream($stream, [IO.Compression.CompressionLevel]::Optimal, $true)
    $input = [IO.File]::OpenRead($appExe)
    $originalLength = $input.Length
    $input.CopyTo($gzip, 1MB)
    $input.Dispose()
    $gzip.Dispose()
    $compressedLength = $stream.Position - $start

    # Footer read by installer\Installer.cs (Payload): version, int32 version length, int64 compressed, int64 original, "SPECTRM1".
    $versionBytes = [Text.Encoding]::UTF8.GetBytes($version)
    $writer = New-Object IO.BinaryWriter($stream, [Text.Encoding]::UTF8, $true)
    $writer.Write($versionBytes)
    $writer.Write([int]$versionBytes.Length)
    $writer.Write([long]$compressedLength)
    $writer.Write([long]$originalLength)
    $writer.Write([Text.Encoding]::ASCII.GetBytes('SPECTRM1'))
    $writer.Flush()
}
finally {
    $stream.Dispose()
}
$size = [math]::Round((Get-Item $out).Length / 1MB, 1)
Write-Host "== Done: $out ($size MB)" -ForegroundColor Green
