# Writing Islet plugins

**Русский: [plugins.ru.md](plugins.ru.md)**

A plugin is a folder with a `plugin.json` in it. Drop the folder into
`%APPDATA%\Islet\plugins`, open *Settings → Plugins* and press **Reload**. That is the whole
install.

There are three kinds of plugin, and one folder can mix them:

| Kind | What you write | Good for |
|---|---|---|
| Shortcuts | a few URL templates in `plugin.json` | `gh islet` → search GitHub |
| Items | ready-made rows in `plugin.json` | commands that open a file, a folder, a URL, copy a snippet |
| Process | a program in any language, talking JSON over stdin/stdout | anything that needs code: live data, calculations, calling an API |

Two plugins ship with Islet and are worth reading as examples:
[`plugins/web-shortcuts`](../plugins/web-shortcuts/plugin.json) (shortcuts only) and
[`plugins/process-killer`](../plugins/process-killer) (a PowerShell process plugin).
[`docs/examples/python-passwords`](examples/python-passwords) uses every part of the protocol.

A plugin in `%APPDATA%\Islet\plugins` with the same `id` as a built-in one replaces it.

## plugin.json

Comments and trailing commas are allowed. Any text field can be a plain string or an object
of translations: `"name": { "en": "Passwords", "ru": "Пароли" }` — Islet picks the interface
language, then English.

```jsonc
{
  "id": "passwords",                 // unique, stays the same across versions
  "name": { "en": "Passwords", "ru": "Пароли" },
  "version": "1.0.0",
  "author": "You",
  "description": "“pw 24” — a strong password",
  "icon": "\uE8D7",                  // a Segoe Fluent Icons glyph, or a picture in the folder: "icon.png"
  "api": 1,                          // protocol version the plugin was written for

  // Process plugins
  "keywords": ["pw"],                // "pw " in the search box scopes the search to this plugin
  "global": false,                   // also answer queries without the keyword (be quick!)
  "answersEmpty": true,              // answer "pw " with nothing typed after it
  "order": 60,                       // position of the group in mixed results; built-ins use 0–100
  "maxResults": 8,
  "timeoutMs": 1500,                 // a slower answer is dropped
  "startup": false,                  // start the process with Islet (for background notifications)
  "run": { "command": "python", "args": ["main.py"] },

  // Items: rows without code
  "items": [
    {
      "title": { "en": "Open the project", "ru": "Открыть проект" },
      "subtitle": "D:\\Work\\islet",
      "keywords": "work islet repo",         // extra words to match, space-separated
      "glyph": "\uE8B7",
      "action": { "open": "D:\\Work\\islet" },
      "confirm": null                        // text of a “press Enter again” confirmation, if any
    }
  ],

  // Shortcuts: "gh query" → a site search
  "shortcuts": [
    { "keyword": "gh", "aliases": ["гх"], "name": "GitHub",
      "url": "https://github.com/search?q={query}",
      "home": "https://github.com",          // opened for an empty "gh "
      "glyph": "\uE943" }
  ]
}
```

`run.command` is looked up in the plugin folder first, then on `PATH`, so both
`"command": "python"` and `"command": "tool.exe"` (shipped next to `plugin.json`) work. In
`args`, `{pluginDir}` becomes the plugin folder with a trailing backslash. The working
directory is the plugin folder.

## Actions

Rows, notifications and live activities carry an action. It is one of:

| Action | What happens |
|---|---|
| `{"open": "…", "args": "…"}` | opens a file, folder, URL or protocol (`ms-settings:`) the way Windows does |
| `{"reveal": "C:\\…\\file"}` | shows the file in its folder |
| `{"app": "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App"}` | starts an app from `shell:AppsFolder` |
| `{"copy": "text"}` | copies the text; the island closes and hands focus back |
| `{"paste": "text"}` | pastes the text into the window the person was in before the island |
| `{"query": "pw 32"}` | types a new query into the island, which stays open |
| `{"invoke": "id", "data": {…}}` | sends `{"type":"invoke","action":"id","data":{…}}` back to your process |

A plain string is short for `open`. After an `invoke` the island closes; put
`"keepOpen": true` into `data` to keep it open (for example after deleting a row, so the
query can be re-run).

## The process protocol

Islet starts `run.command` once — on the first query, or at launch with `startup: true` —
and keeps it running. Messages are JSON objects, **one per line**, UTF-8, in both
directions. Write nothing else to stdout; stderr is copied into Islet's log (the first 50
lines per run), so use it for debugging.

Environment: `ISLET_API=1`, `ISLET_PLUGIN_DIR`, `ISLET_PIPE` (see [protocol.md](protocol.md)),
`PYTHONIOENCODING=utf-8`, `PYTHONUNBUFFERED=1`.

### From Islet

```json
{"type":"hello","api":1,"islet":"0.2.0","language":"ru","pluginDir":"C:\\…\\passwords\\"}
{"type":"query","id":7,"text":"24","scoped":true}
{"type":"invoke","action":"batch","data":{"length":24}}
{"type":"shutdown"}
```

- `query.text` is what follows the keyword; `scoped` is `false` when a `global` plugin is
  asked about an ordinary search.
- Answer every query with the same `id`. Islet asks again on every pause in typing and drops
  answers to old ids, so there is no need to cancel anything.
- After `shutdown` you have 300 ms to exit before the process is killed.

### To Islet

```jsonc
// Results for a query
{"type":"results","id":7,"items":[
  {
    "id": "stable-id",                // optional; keeps the row steady between answers
    "title": "k8#Vq…",
    "subtitle": "24 characters",
    "trailing": "Enter to copy",      // small text on the right
    "glyph": "\uE8D7",                // or "icon": "https://…" / "icon.png" / "iconPath": "C:\\…\\app.exe"
    "confirm": null,                  // ask for a second Enter before the action
    "action": {"copy": "k8#Vq…"},
    "actions": [                      // extra context-menu items
      {"title": "Paste", "glyph": "\uE77F", "action": {"paste": "k8#Vq…"}}
    ]
  }
]}

// A notification: the island opens itself into a peek, then it goes to the bell
{"type":"notify","title":"Build finished","body":"42 s","glyph":"\uE930","action":{"open":"C:\\out"}}

// A live activity in the collapsed capsule; send again to update, "clear" to remove
{"type":"activity","id":"build","text":"Building 40%","progress":0.4,"color":"#3BE5CE"}
{"type":"activity","id":"build","clear":true}

// A line in Islet's log
{"type":"log","message":"cache warmed up"}
```

`notify`, `activity` and `log` can be sent at any time, not only in response to a query —
that is what `startup: true` is for: a plugin that watches something and reports on the
island.

### Being a good citizen

- **Answer fast.** A scoped query waits up to `timeoutMs`; a `global` one competes with apps
  and files, so keep it well under 100 ms or leave `global` off.
- **Crashing is tolerated, looping is not.** A process that exits is restarted on the next
  query (not more often than every five seconds). Three exits within a minute switch the
  plugin off with an error in the settings until the next reload.
- Activities disappear when the plugin exits or is switched off.

### PowerShell tip

Windows PowerShell reads redirected stdin in the console code page. Start the script with:

```powershell
$utf8 = [System.Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = $utf8
[Console]::OutputEncoding = $utf8
```

and save the `.ps1` as UTF-8 **with BOM**, or non-Latin strings in the script itself turn
into mojibake.

## Checklist

1. `plugin.json` parses (Islet shows the error on the plugin's card if not).
2. A keyword that does not clash with the built-in ones — type `?` in the island to see them.
3. The process answers `{"type":"query",…}` from a console:
   `echo {"type":"query","id":1,"text":"x","scoped":true} | python main.py`.
4. *Settings → Plugins → Reload*, then type your keyword and a space.
