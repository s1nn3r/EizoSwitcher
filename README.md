# EizoSwitcher

Automatically switch the color mode of EIZO ColorEdge monitors over USB depending on which programs are running — e.g. `BT.709` while DaVinci Resolve is open, `CAL` for Photoshop, `sRGB` for games.

**ColorNavigator does not need to be installed.** EizoSwitcher talks to the monitor directly via its USB HID interface (the USB upstream cable between PC and monitor must be connected).

> Unofficial project, not affiliated with or endorsed by EIZO Corporation. Use at your own risk.

## Tested hardware

| Model  | Status |
|--------|--------|
| CG279X | read/switch mode, read mode names ✔ |
| CS2731 | read mode, read mode names ✔ |

Other ColorEdge/FlexScan models that expose the `EIZO USB HID Monitor` device (VID `0x056D`) very likely work the same way. Reports welcome.

## Usage

Requires Windows and the .NET 10 runtime.

```
EizoSwitcher list                          connected monitors + current mode
EizoSwitcher modes                         all color modes per monitor
EizoSwitcher set <model|serial> <mode>     switch mode (name or index), e.g. set CG279X BT.709
EizoSwitcher run [config.json]             background mode: switch by running processes
```

`run` has no window; it writes `EizoSwitcher.log` next to the executable. To start it with Windows, put a shortcut to `EizoSwitcher.exe run` into `shell:startup`.

## Configuration

`config.json` (next to the executable, reloaded automatically when changed):

```jsonc
{
  "pollSeconds": 2,
  // mode when no rule applies; key = model, serial or "*"
  "default": { "CG279X": "CAL", "CS2731": "User" },
  // first matching rule (top to bottom) wins, per monitor
  "rules": [
    { "name": "Video",  "processes": [ "Resolve", "Adobe Premiere Pro" ], "modes": { "CG279X": "BT.709" } },
    { "name": "Photo",  "processes": [ "Photoshop", "Lightroom" ],        "modes": { "CG279X": "CAL" } },
    { "name": "Gaming", "paths": [ "*\\steamapps\\common\\*" ],            "modes": { "*": "sRGB" } }
  ]
}
```

- `processes`: process names without `.exe` (case-insensitive)
- `paths`: wildcard patterns on the full executable path
- modes by name as shown by `EizoSwitcher modes`, or by index

EizoSwitcher only switches when the *desired* mode changes, so a mode you select manually on the monitor stays until a rule starts or stops matching.

### Note on ICC profiles

ColorNavigator also switches the Windows display ICC profile together with the mode. EizoSwitcher currently only switches the monitor's mode. With ColorNavigator running in parallel, it picks up the change itself; without it, the Windows profile stays as it is.

## Protocol

Reconstructed from `libmctrl_x64.dll` / `libemc_x64.dll` of ColorNavigator 7.2.9 and verified on hardware. See [PROTOCOL.md](PROTOCOL.md).

## License

MIT, see [LICENSE](LICENSE).
