# FanControl.DRGB

USB HID fan control and RPM feedback for DeepRGB V5F controllers, implemented as a [FanControl plugin](https://github.com/Rem0o/FanControl.Releases/wiki/Plugins).

[中文安装与使用说明](README.zh-CN.md) · [Protocol notes](docs/protocol.md) · [Hardware validation](VALIDATION.zh-CN.md)

## Compatibility

- FanControl V283 / .NET 10; uses the host's FanControl.Plugins and HidSharp assemblies.
- DeepRGB U16 V5F (`2486:3217`), firmware **0.9.212.38**, 10 channels: hardware tested.
- C16/S16 V5F IDs are recognized but have not been tested on hardware.
- Positive targets use runtime command `99`, renewed about once per second.
- Verified U16 firmware uses fan-only command `CC` for zero targets and mode transitions. Repeated hardware tests preserved lighting playback.
- **0% does not guarantee fan stop.** The tested fans still rotate around 890 RPM at a zero target.

## Install

Download `FanControl.DRGB-0.1.3.zip` from [Releases](https://github.com/Rin010/FanControl.DRGB/releases), close DRGB and FanControl, then run the archive's `install.ps1` or copy `FanControl.DRGB.dll` into FanControl's `Plugins` directory. Enable **DeepRGB V5F** and refresh sensors. Keep DRGB closed while FanControl owns the controller.

## Build

Install the .NET 10 SDK and FanControl V283, then run:

```powershell
./build.ps1 -FanControlDir 'C:\Program Files (x86)\FanControl'
```

The build runs 43 offline tests, compiles the standalone diagnostics tool and creates binary/source archives in `dist/`. Offline tests do not open physical HID devices. The standalone diagnostic tool can change real fan output when a hardware exercise is explicitly selected.

This repository contains original plugin source and protocol findings. Raw local captures, device identifiers, host configuration, firmware images and third-party binaries are excluded. MIT licensed.
