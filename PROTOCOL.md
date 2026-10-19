# EIZO USB HID control protocol (as far as known)

The monitor enumerates as `EIZO USB HID Monitor`, USB VID `0x056D` (PID depends on model, e.g. `0x403E` CG279X, `0x4048` CS2731). All communication uses **feature reports** on the vendor usage page `0xFF30`.

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

- **Get**: `SetFeature(0x03, page, usage, pin)`, then `GetFeature(0x03)`. The response repeats `page`, `usage`, `pin` followed by the value. If the header does not match, retry (ColorNavigator retries up to 10 times with ~80–110 ms delay).
- **Set**: `SetFeature(0x02, page, usage, pin, value)`.

The `page:usage` pair is a 32-bit control ID, e.g. `0xFF0000F3` → page `0xFF00`, usage `0x00F3`.

## Known controls

| Control ID | Access | Meaning |
|------------|--------|---------|
| `0xFF0000F3` | get/set (i32) | Current color mode index |
| `0xFF0100DB` | set (i32) | Mode-name selector (index) |
| `0xFF0100DC` | get (bytes) | Name of the selected mode (ASCII, NUL terminated) |

Reading mode names past the last mode returns the last name again, which can be used to determine the mode count.
