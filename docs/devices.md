# Device battery: adding yours

The island shows the charge of wireless mice, keyboards, headsets and gamepads: `bat ` in search,
a peek when the battery runs low and a reminder in the capsule when it's nearly flat. It gets the
charge from:

| Source | Covers |
|---|---|
| **Logitech HID++** | Logitech mice, keyboards and headsets, through a receiver (Unifying, Lightspeed, Bolt) or directly. Built in. |
| **Bluetooth** | devices whose battery Windows already knows (shown in *Settings → Bluetooth*). Built in. |
| **Recipes** | everything else. A recipe describes a device's protocol as data, no code — anyone can write one. |

Recipes live in [`devices/devices.json`](../devices/devices.json). The island fetches this database
from the repository once a day, so a new device reaches everyone without a new release.

## How it works

With wireless devices you can almost always ask the receiver for the charge: send a short packet
to its vendor HID collection and read the reply. Every vendor does it differently, but the shape is
the same, and a recipe is exactly that shape:

```json
{
  "id": "ajazz-aj159-apex",
  "name": "Ajazz AJ159 APEX",
  "kind": "mouse",
  "connection": "dongle",
  "match": { "vid": "3151", "pid": "5007", "page": "FFFF", "usage": "0002" },
  "steps": [
    { "setFeature": "00 F7" },
    { "wait": 40 },
    { "getFeature": "05", "require": { "1": "00", "2": "00" }, "battery": { "at": 3, "valid": [1, 100] } }
  ],
  "source": "https://github.com/Aiacos/ajazz-control-center"
}
```

## 1. Find the protocol

Look for an existing one first — chances are someone has done it:

- [HeadsetControl](https://github.com/Sapd/HeadsetControl) — SteelSeries, Corsair, HyperX, Logitech, Audeze headsets and more;
- [rivalcfg](https://github.com/flozz/rivalcfg) — SteelSeries mice;
- [OpenRGB](https://gitlab.com/CalcProgrammer1/OpenRGB), [Solaar](https://github.com/pwr-Solaar/Solaar),
  and a GitHub search for your receiver's `VID:PID` plus the word `battery`.

These projects use hidapi, and recipes behave exactly like it, so bytes carry over as they are:
`hid_write(dev, {0x06, 0x18})` is `{ "write": "06 18" }`, `hid_read` is `{ "read": {} }`, and the
reply indexes are the same.

If there's nothing yet, watch the vendor's software: [Wireshark](https://www.wireshark.org/) with
USBPcap, filter by the receiver's address, and look for the packet after which the app updates
its percentage. Draining the battery by a few percent and comparing replies helps.

## 2. See the device the way the island does

*Settings → Integrations → Devices → **Report***. The island polls everything connected and opens
a file with:

- every HID collection: `VID:PID`, interface number (`MI`), usage page and usage, report lengths;
  vendor collections (page `FF00` and up) are marked — those are the only ones you can talk to;
- for every recipe, what was sent and what came back, byte by byte.

## 3. Write a recipe and test it locally

*Settings → Integrations → Devices → **Your devices.json*** opens `%APPDATA%\Islet\devices.json`.
The island reloads it as soon as you save; a recipe with the same `id` as one in the shared
database replaces it. Check the result in `bat ` and in the report.

### Fields

| Field | Meaning |
|---|---|
| `id` | unique name: Latin letters, digits, `-` `_` `.` |
| `name` | how the device is shown |
| `kind` | `mouse`, `keyboard`, `headset`, `gamepad`, `other` |
| `connection` | `dongle` (default), `wired`, `bluetooth` — the label in results |
| `match.vid`, `match.pid` | hex; `pid` can be a list |
| `match.page`, `match.usage` | the collection: vendor-defined only (`FF00`–`FFFF`) or `000C` |
| `match.interface` | the interface number (`MI` in the report) — when models differ in usage page |
| `steps` | steps in order; without `steps` the device is only marked as connected |
| `source` | where the protocol comes from — a link to a project or your own notes |

### Steps

| Step | What it does |
|---|---|
| `{ "write": "06 18" }` | an output report; byte 0 is the report id (`00` for unnumbered reports); padded with zeros |
| `{ "read": { "timeout": 1000, "attempts": 8 } }` | waits for an input report; unnumbered reports lose the leading `00`, like hidapi |
| `{ "setFeature": "00 F7" }` | a feature report |
| `{ "getFeature": "05" }` | read the feature report with this id; the id stays in byte 0 |
| `{ "wait": 40 }` | a pause, up to 500 ms |
| `{ "flush": true }` | drop queued input reports so an old reply isn't taken for a new one |

`write` can take `"length": 20` (pad the packet) and `"checksum": { "from": 0, "to": 18, "at": 19 }`
(sum of bytes modulo 256).

`read` and `getFeature` take the parsing rules. Bytes are given by index, values in hex;
`"4A/7F"` means "the byte masked with 7F equals 4A", a list means "any of":

| Field | Meaning |
|---|---|
| `match` | (`read` only) not the right report — read the next one |
| `require` | doesn't match — the charge is unknown |
| `offline` | matches — the device isn't connected (headset off, base station plugged in) |
| `charging` | `{ "at": 4, "equals": ["01"] }` or `{ "at": 1, "mask": "80" }` — charging |
| `battery` | where the charge is — see below |

`battery`:

| Field | Meaning |
|---|---|
| `at` | byte index |
| `mask` | a mask such as `7F` when the top bit is a flag |
| `word` | `be` or `le` — two bytes (voltage in mV) |
| `valid` | `[min, max]` — a raw value outside means "unknown" (not 0%!) |
| `range` | `[min, max]` — scale linearly to percent: levels `[0, 4]`, `[0, 8]` |
| `curve` | `[[raw, %], …]` — a curve, usually voltage → percent |

### Limits

A recipe sends bytes straight to a device, so the island is strict:

- vendor collections (and `000C`) only — keyboards and mice themselves are off limits;
- up to 16 steps, packets up to 64 bytes, pauses up to 500 ms, reads up to 1.5 s, the whole recipe within 3 seconds;
- no answer — the wait before the next try grows up to half an hour.

**Recipes only ask for state.** No commands that change anything in the device: DPI, lighting,
modes, firmware. If you aren't sure a byte only asks, don't send it.

## 4. Send it to the shared database

Open a pull request that changes `devices/devices.json`:

- the recipe is tested on your device — say which model and what the island showed;
- `source` says where the protocol comes from;
- bump `revision` at the top of the file by 1.

Every recipe is reviewed by hand before it goes in.
