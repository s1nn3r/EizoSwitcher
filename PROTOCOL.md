# EIZO USB HID control protocol (as far as known)

Reconstructed from `libmctrl_x64.dll` / `libemc_x64.dll` of ColorNavigator 7.2.9 and verified on a CG279X and a CS2731.

The monitor enumerates as `EIZO USB HID Monitor`, USB VID `0x056D` (PID depends on the model, e.g. `0x403E` CG279X, `0x4048` CS2731). All communication uses **feature reports** on the vendor usage page `0xFF30`.

## Feature reports

| Report ID | Size (excl. ID) | Purpose |
|-----------|-----------------|---------|
| `0x02` | 38  | **Set** request (short) |
| `0x03` | 38  | **Get** request / response (short) |
| `0x04` | 528 | Set request (long payload) |
| `0x05` | 528 | Get request / response (long payload) |
| `0x06` | u16 | **PIN** – must be sent with every request |
| `0x08` | 24  | Identity: ASCII serial (8) + model (16, space padded) |
| `0x09` | var | Capability list (TLV) |
| `0x0A` | u16 | Extended-mode flag; if non-zero the `0x0B`–`0x0E` variants are used |
| `0x0B`–`0x0E` | 40 / 530 | Like `0x02`–`0x05` with an additional leading u16 field |

## Request layout (little endian)

```
[report id] [page u16] [usage u16] [pin u16] [data ...]
```

- **Get**: `SetFeature(0x03, page, usage, pin)`, then `GetFeature(0x03)`. The response repeats `page`, `usage`, `pin` followed by the value. If the header does not match, retry (ColorNavigator retries up to 10 times with ~80–110 ms delay). Unknown controls answer with zeros.
- **Set**: `SetFeature(0x02, page, usage, pin, value)`.

The `page:usage` pair is a 32-bit control ID, e.g. `0xFF0000F3` → page `0xFF00`, usage `0x00F3`. Besides EIZO's own pages, the standard **VESA monitor control** usages (page `0x0082`, same numbers as DDC/CI VCP codes) are reachable through the same tunnel.

## Known controls

| Control ID | Access | Meaning |
|------------|--------|---------|
| `0xFF0000F3` | get/set (i32) | Current color mode index |
| `0xFF0000F4` | get (bytes) | Enabled modes: one byte per mode index, `1` = enabled (offered when cycling modes on the monitor), `0` = disabled ("mode skip") |
| `0xFF0100DB` | set (i32) | Mode-name selector (index) |
| `0xFF0100DC` | get (bytes) | Name of the selected mode (NUL terminated) |
| `0x00820010` | get | Brightness (VESA VCP 0x10; raw value, e.g. 247 on a CG279X) |
| `0x00820012` | get | Contrast (VESA VCP 0x12) |

- Reading mode names past the last mode returns the last name again, which gives the mode count.
- Mode names use ASCII plus `0x01` for **®** (`Adobe\x01RGB` = "Adobe®RGB").
- Power on/off is not a plain control ID; ColorNavigator uses an internal control table (`EmCtrlSetPower`, control `0x4A`). VESA `0x008200D6` reads 0 on ColorEdge models.

## Matching USB devices to Windows displays

The ASCII serial in report `0x08` equals the monitor's EDID serial. EIZO ColorEdge models do not include a serial text descriptor (`0xFF`) in their EDID – the number is only in the numeric serial field (bytes 12–15), which is also what Windows shows via WMI (`WmiMonitorID.SerialNumberID`). EizoSwitcher uses the text descriptor if present, otherwise the numeric field.

## DDC/CI

EIZO ColorEdge CG279X and CS2731 did not answer DDC/CI requests (`dxva2`) in testing; a FlexScan S1721 did (VCP `0xD6` power mode).
