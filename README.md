# SPECTRM - Audio Meter

SPECTRM is a native Windows desktop real-time audio meter built with .NET Windows Forms. It does not use a browser or a web view.

## Run

On Windows with the .NET 7 SDK installed:

```powershell
dotnet run --project .\MeteringTool.csproj
```

The first build restores NAudio from NuGet. To create a distributable Windows executable:

```powershell
dotnet publish .\MeteringTool.csproj -c Release
```

The Release publish is a self-contained, single-file x64 Windows app. Run `SPECTRM.exe` directly; no separate .NET Desktop Runtime installation or companion runtime files are required.

## Installer

To build the one-file installer for sharing:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\build-installer.ps1
```

This creates `dist\SPECTRM-Setup-<version>.exe` (about 63 MB, with the app compressed inside). The version comes from `<Version>` in `MeteringTool.csproj`. The setup program lives in `installer\` and targets .NET Framework 4.8, which is built into Windows 10 and 11, so nothing else needs to be installed.

- Installs per user to `%LOCALAPPDATA%\Programs\SPECTRM` by default, with no admin prompt. The folder can be changed.
- Adds optional desktop and Start menu shortcuts, plus an entry in Settings > Apps for uninstalling.
- Running a newer installer over an existing install updates it, closing SPECTRM first if it is open.
- `Uninstall SPECTRM.exe` in the install folder, or Settings > Apps, removes the app and shortcuts. Settings are kept unless you choose to delete them.
- Command line: `/quiet` for a silent install, `/dir="C:\path"` to choose the folder, `/uninstall [/quiet]` to uninstall.

The installer isn't code-signed, so Windows SmartScreen may show "Windows protected your PC" the first time someone runs it. They can click "More info", then "Run anyway".

SPECTRM monitors the sound being played through the current Windows default output using low-latency WASAPI loopback. It does not capture or listen to the microphone. It follows changes to the Windows default playback device while the app is open. Audio must be playing through that output for the meters to move. The idle visualization is rate-limited in Unlimited mode to prevent needless redraws when no audio is playing.

The wide dashboard puts a spectrogram first, followed immediately by a K-weighted momentary LUFS meter and an analog-style VU meter. Waveform, vectorscope, short-term loudness history, stereo correlation, and spectrum visualizers complete the single-row display. The VU scale is relative to digital full scale; it is not calibrated to a particular analog reference level. Chart gridlines and panel borders are removed for a clean, uninterrupted display. An 8,192-sample FFT, frequency smoothing, and frame-to-frame smoothing give the spectrum more detail and a steadier, smoother curve. A minimal black-and-white palette with restrained purple accents, evenly aligned headings, and double-buffered drawing keep it crisp while resizing. Hover over the output status for device information or errors. This is a standalone monitor of the Windows default playback output, not a VST/AU plugin hosted inside a DAW. Applications that send audio directly to an exclusive or separate ASIO output may not be available through Windows loopback.

The standard Windows title bar is replaced with a frameless window. A visible MENU handle and a 48-pixel top-edge hover area reveal the toolbar; it remains open briefly as the pointer moves to a control. The wider toolbar includes a labeled frame-rate button showing the current rate and custom minimize, maximize, and close buttons. Resize by dragging any window edge or corner; the borderless window keeps the native Windows sizing frame and a wider edge target. Use Ctrl+Shift+M to toggle the toolbar, Alt+S to open frame-rate settings directly, or Esc to hide the toolbar. Settings lets you select 30, 60, 120, or Unlimited FPS in the centered settings window. The selected rate is saved in `%LOCALAPPDATA%\SPECTRM\settings.txt` and restored the next time the app opens. Higher frame rates can use more CPU; audio capture and analysis continue independently of the display refresh rate.
