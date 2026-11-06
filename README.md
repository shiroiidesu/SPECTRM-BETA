# SPECTRM

SPECTRM is a real-time audio meter for Windows. It sits as a slim strip on your screen and shows what your computer is playing: loudness, frequency content, stereo image and waveform, all at once. It works with any app (a DAW, Spotify, YouTube, a game) without plugins, virtual cables or routing.

> **Inspiration:** SPECTRM is heavily inspired by [MiniMeters](https://minimeters.app/), a great metering app with a compact strip of meters. Go check it out and support it.

## How it works

- **Capture:** SPECTRM listens to your default output device through Windows' WASAPI loopback. It hears exactly what you hear, and it follows you when you switch speakers or headphones.
- **Analysis:**
  - An 8192-point FFT with a Hann window drives the spectrum and spectrogram.
  - Loudness uses ITU-R BS.1770 K-weighting, the same filters as the EBU reference implementation, accurate to 0.01 dB on the EBU test tones.
  - Short-term loudness is the standard 3-second EBU R128 window.
- **Drawing:** every panel is drawn by SPECTRM itself, paced by a high-resolution frame clock so the frame rate you pick is the one you get.

## Meters

| Panel | What it shows |
|---|---|
| **Spectrogram** | Frequency content scrolling over time, from 20 Hz to 20 kHz |
| **Waveform** | Scrolling peaks, coloured by how bright or bassy the sound is |
| **Level bars** | Left bar: sample peak in dBFS with peak hold. Right bar: momentary loudness in LUFS. |
| **Vectorscope** | Stereo image: width, balance and phase |
| **VU meter** | Short-term loudness (LUFS) on an analog needle that moves like a real VU meter. 0 VU = −9 LUFS. |
| **Oscilloscope** | The live waveform |
| **Spectrum** | Smoothed frequency curve. Hover over it to read the level, frequency and musical note. |

## Features

- **Layouts:** a horizontal strip or a vertical column (Alt+L)
- **Rearrangeable panels:** drag a panel by its grip (or Ctrl+drag) to reorder; the order is saved
- **Themes:** five colour themes: Deep Sea, Aurora, Synthwave, Glacier and Graphite
- **Always on top:** pin it over your DAW or game (Alt+P)
- **Frame rate:** 30, 60, 120 fps or unlimited
- **Minimal window:** borderless; the toolbar slides down when your cursor reaches the top
- **Settings saved:** everything is remembered between launches
- **One-file installer:** installs per user, needs no admin rights, and includes an uninstaller

## Install

Run `SPECTRM-Setup-1.0.0.exe` and follow the steps. Requires Windows 10 or 11 (64-bit); no .NET install needed.

To build it yourself, run `tools\build-installer.ps1`. It publishes the app and writes the installer to `dist\`.

## Built with

C# / .NET 7 Windows Forms and [NAudio](https://github.com/naudio/NAudio).
