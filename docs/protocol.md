# Talking to Islet from other programs

Any program, script or scheduled task can put a notification or a live activity on the
island. There are two ways in, and they carry the same messages.

## Command line

```powershell
Islet.exe --notify "Backup finished" "312 files, 1.4 GB" "Backup"
Islet.exe --activity build "Building…" 0.4
Islet.exe --activity build "Building… 80%" 0.8
Islet.exe --activity-clear build
Islet.exe --open "k naruto"          # open the island with a query
Islet.exe --settings plugins         # open a settings page
Islet.exe --quit
```

If Islet is running, the second `Islet.exe` hands the message to it over the pipe below and
exits within milliseconds, without loading any UI. If it is not running, Islet starts and
then shows the message.

| Switch | Arguments |
|---|---|
| `--notify` | title, body (optional), source label (optional) |
| `--activity` | id, text, progress 0…1 (optional) |
| `--activity-clear` | id |
| `--open` | query (optional) |
| `--settings` | `general`, `behavior`, `notifications`, `look`, `search`, `pins`, `integrations`, `plugins`, `about` |
| `--demo` | query — opens the island without taking focus (for screenshots) |
| `--quit` | — |

## Named pipe

`\\.\pipe\Islet.<user name>` — for example `\\.\pipe\Islet.Rayness`. The pipe accepts
connections from the same Windows user only. Write one JSON object per line, UTF-8:

```json
{"type":"notify","title":"Backup finished","body":"312 files","source":"Backup","glyph":"\uE8F7","action":{"open":"D:\\Backup"}}
{"type":"activity","id":"backup","text":"Backup 40%","progress":0.4,"color":"#3BE5CE"}
{"type":"activity","id":"backup","clear":true}
{"type":"open","query":"cb "}
```

From PowerShell:

```powershell
$pipe = [System.IO.Pipes.NamedPipeClientStream]::new(".", "Islet.$env:USERNAME", "Out")
$pipe.Connect(2000)
$writer = [System.IO.StreamWriter]::new($pipe, [System.Text.UTF8Encoding]::new($false))
$writer.WriteLine('{"type":"notify","title":"Hello from PowerShell"}')
$writer.Dispose()
```

From Python:

```python
import json, os
with open(rf"\\.\pipe\Islet.{os.environ['USERNAME']}", "w", encoding="utf-8") as pipe:
    pipe.write(json.dumps({"type": "notify", "title": "Hello from Python"}) + "\n")
```

## Messages

| `type` | Fields |
|---|---|
| `notify` | `title`, `body`, `source` (label in the bell), `glyph` (Segoe Fluent Icons) or `icon` (`https://…`, `ms-appx:///…`, a file path), `action`, `id` (the same id is not shown twice), `silent` (bell only, no peek) |
| `activity` | `id`, `text`, `progress` (0…1), `glyph`, `icon`, `color` (`#RRGGBB`), `action`, `clear` |
| `open` | `query` |
| `settings` | `page` |
| `log` | `message` |

Actions are described in [plugins.md](plugins.md#actions). Actions that come through the
pipe can open, reveal, copy and paste; `invoke` works only for plugins.

A notification opens the collapsed island into a *peek*: an icon, the title and the body
(scrolling if it is too long), held for a few seconds, then it collapses back — and stays
in the bell. The person chooses in the settings whether peeks happen at all, how long they
stay and whether they beep.
