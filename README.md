# Islet

**Русский: [README.ru.md](README.ru.md)**

A small island at the top edge of your Windows screen: search your apps and every file on every
drive, and keep a few buttons within reach. Collapsed, it is a thin capsule a couple of
centimetres wide. Point at it — or press a hotkey — and it opens into a search box.

![Islet, open with search results](docs/island.png)

No background service, no account, nothing to configure before it works: one app and a
settings file.

## What it does

- **Finds apps** — everything in `shell:AppsFolder`, Microsoft Store apps included.
- **Finds files on every drive.** Windows Search only indexes your user folders on the system
  drive; Islet keeps its own index of file names for the other drives, so a file on `D:` comes
  up as fast as one on your desktop.
- **Falls back to the web** in the last row of results: Yandex, Google, Bing or DuckDuckGo.
- **Keeps buttons** for quick launch — up to six: an app, a file, a folder or a link.
- **Stays out of the way.** Opening on hover does not steal focus: your cursor and caret stay
  in whatever you were typing in. Over a fullscreen game or video, the island hides.
- **Glass.** The backdrop is the blurred desktop, cut to the shape of the capsule. Tint density
  is adjustable and the effect can be turned off.
- **Speaks English and Russian**, following your Windows display language, and you can force
  either one in the settings.

## Install

Grab the latest build from [Releases](https://github.com/Rayness/islet/releases):

| File | What it is |
|---|---|
| `Islet-win-Setup.exe` | Installer, x64. Adds a Start menu shortcut, an entry in Installed apps, and keeps itself updated. |
| `Islet-win-Portable.zip` | Portable, x64. Unpack anywhere and run `Islet.exe`. Updates by hand. |
| `Islet-win-arm64-Setup.exe` | The installer for ARM64 machines. |
| `Islet-win-arm64-Portable.zip` | The portable build for ARM64 machines. |

Everything is bundled, so you do **not** need .NET or the Windows App SDK installed. The builds
are not code-signed yet, so SmartScreen will warn on first run: *More info → Run anyway*.

Requires Windows 10 version 2004 (build 19041) or newer, x64 or ARM64.

## How to use it

The island lives in three states:

| State | How to get there | What happens |
|---|---|---|
| Collapsed | the default | a thin capsule at the top centre, above other windows |
| Hover | point at it | it opens, focus stays in your previous window; move away and it collapses |
| Open | the hotkey, `Ctrl + Alt + Space` by default | it opens with the cursor in the search box; press again or `Esc` to collapse and hand focus back |

In the results:

| Key | Action |
|---|---|
| `↑` / `↓` | pick a result |
| `Enter` | open it |
| `Ctrl + Enter` | show the file in its folder |
| `Esc` | collapse the island |

Right-click a result for *Show in folder* and *Pin to the island*. Right-click a button on the
left to unpin it. The `…` button on the right opens the settings and can quit the island.

## How the search works

Results come in order: apps → files and folders → *search the web*. Files arrive from two
sources at once:

- **Windows Search**, through OLE DB against the system index. Fast and content-aware, but it
  only covers what Windows itself indexes — normally your user folders on the system drive.
- **Islet's own drive index** — a walk over the non-system drives (or whichever folders you
  list in the settings) that remembers file names. It lives in one binary file, is read back at
  startup, is rebuilt once a day and whenever the folder list changes, and `FileSystemWatcher`
  catches what changes in between. Lookups run in parallel over one shared character buffer,
  with no allocation per name.

The drive index can be turned off, leaving only Windows Search.

## Settings

![Settings](docs/settings.png)

| Section | What is in it |
|---|---|
| General | hotkey (recorded by pressing it), start with Windows, web search engine, language |
| Behavior | open on hover, open and close delays, hide the collapsed capsule, hide over fullscreen windows, which monitor to live on |
| Appearance | glass, background density, island width, collapsed capsule size, number of result rows, clock |
| Search | drive index on/off, its status and a rebuild button, folder list, exclusions, a link to Windows search settings |
| Buttons | order, removal, adding an app, a file, a folder or a link |

Every change is saved as you make it; there is no Apply button.

## Your data

Settings live in `%APPDATA%\Islet\`, throwaway things in `%LOCALAPPDATA%\Islet.cache\`:

| File | Where | What it is |
|---|---|---|
| `settings.json` | `%APPDATA%\Islet` | settings |
| `pins.json` | `%APPDATA%\Islet` | buttons; safe to edit by hand, Islet re-reads it |
| `drive-index.bin` | `%LOCALAPPDATA%\Islet.cache` | the drive index cache; delete it and it rebuilds |
| `island.log` | `%LOCALAPPDATA%\Islet.cache` | state changes and search errors, capped at 512 KB |

The installer owns `%LOCALAPPDATA%\Islet` and wipes it on every install, which is exactly why
nothing of yours is kept there. Uninstalling leaves your settings behind.

Islet sends nothing anywhere. The only outbound traffic is what you ask for: the *search the
web* row, a link button you open, and the update check against GitHub.

## Building from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0); Visual Studio is
not required.

```powershell
git clone https://github.com/Rayness/islet.git
cd islet
dotnet build src/Islet/Islet.csproj -c Debug
src/Islet/bin/Debug/net10.0-windows10.0.22621.0/win-x64/Islet.exe
```

To build what the releases ship — installer, portable zip and update package — install the
[Velopack](https://velopack.io) CLI once with `dotnet tool install -g vpk`, then:

```powershell
build/pack.ps1                       # or: build/pack.ps1 -Version 0.2.0 -Runtime win-arm64
```

Two command-line switches help when looking at the UI without touching keyboard or mouse:

- `--demo <query>` opens the island with a query already typed, without taking focus;
- `--settings [general|behavior|look|search|pins|about]` opens the settings on a given page.

Only one instance runs at a time: a second launch exits immediately.

## How it is put together

| Where | What |
|---|---|
| `IslandWindow.xaml(.cs)` | the island: three states, an animated capsule inside a transparent window, keyboard and results |
| `SettingsWindow.xaml(.cs)` | the settings window |
| `Settings/` | the settings model and `SettingsStore` with its `Changed` event |
| `Native/WindowHost.cs` | window message interception: the hotkey and trimming the system frame (`WM_NCCALCSIZE`, `WM_NCACTIVATE`, `WM_STYLECHANGING`) |
| `Native/IslandBackdrop.cs` | the backdrop: a blurred desktop (`HostBackdropBrush`) masked to the capsule's shape |
| `Search/DriveIndex.cs` | the file name index: one shared character buffer, parallel lookup, on-disk cache, `FileSystemWatcher` |
| `Search/AppIndex.cs`, `FileSearch.cs` | apps from `shell:AppsFolder`, files through OLE DB against Windows Search |
| `Shell/` | shell icons, launching things, start with Windows, updates |
| `Pins/` | the buttons and their storage |
| `Strings/` | the interface in `.resw` files, one folder per language |
| `tools/make-icon.ps1` | draws `Assets/islet.ico` |

Built on WinUI 3 (Windows App SDK 1.8), .NET 10 and Win2D — the last one only to describe the
glass effect. Updates go through Velopack. There is no MSIX packaging.

### Translating

Every string lives in `src/Islet/Strings/<language>/Resources.resw`. To add a language, copy the
`en-US` folder, translate the values, and add the language to the picker in
`SettingsWindow.xaml.cs` (`LanguageOptions`).

## License

MIT — see [LICENSE](LICENSE).
