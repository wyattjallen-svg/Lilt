# Lilt

A lighter way through the web: a personal, customizable Windows 11 browser built on WebView2 and WinUI 3.

v0.3 prototype: the Lilt design language (Minimal, Glass and Light themes in a sage palette), a toolbar, sidebar and home tab whose every element can be moved, hidden or restyled from the Customize panel (Ctrl+Shift+,), tabs with saved sessions and pinning, bookmarks and import, uBlock Origin Lite built in, Proton Pass one click away, and an efficiency mode that gets out of the way of games and other apps.

- Architecture and decisions: [docs/architecture.md](docs/architecture.md)
- Prototype results, test checklist and limitations: [docs/prototype-report.md](docs/prototype-report.md)

## Run a build

CI publishes every push to `prototype` as the **prototype-build** pre-release. Download `Lilt-win-x64.zip` (about 90 MB), unzip it anywhere and run `Lilt.exe`. It is self-contained (no .NET or Windows App SDK install needed) and uses the WebView2 Runtime that ships with Windows 11.

```
Lilt.exe [url] [--url <url>] [--theme <file>] [--user-data <folder>] [--inprivate]
         [--install-extension <Web Store link or id>] [--no-extensions]
         [--user-agent "<ua>"] [--browser-args "<chromium switches>"] [--startup-report <file>]
```

## Build from source

Requires Windows 11 x64 and the .NET 10 SDK.

```powershell
dotnet run --project tests/Lilt.Core.Tests          # core tests (also run on Linux/macOS)
dotnet publish src/Lilt.App/Lilt.App.csproj -c Release -r win-x64 -p:Platform=x64 -o out/Lilt
.\out\Lilt\Lilt.exe
```

## Your data

Everything lives in `%LOCALAPPDATA%\Lilt`:

| Path | What |
|---|---|
| `theme.json` | Everything you can customize: colors, materials, sizes, the order and visibility of toolbar items, sidebar sections and home elements, and power settings. The Customize panel writes it; you can also edit and save it and the window updates live. `theme.schema.json` beside it gives editor completion; `minimal.json`, `glass.json` and `light.json` are the presets. |
| `session.json` | Open tabs, restored on the next start. |
| `bookmarks.json` | Bookmarks. A bookmark folder named `Home` picks the home tab's tiles. |
| `user\` | Per-site CSS and JS: `example.com.css`, `github.com.js`, `all.css`. Reloaded on save. |
| `newtab\` | The home tab. Lilt refreshes its own files on start; put changes in `custom.css` and `custom.js`. A background image you pick is copied here; `icons\` holds favicons for your tiles. |
| `Extensions\` | Installed extensions (uBlock Origin Lite, anything from the Chrome Web Store). |
| `WebView2\` | Browser profile (cookies, cache, history, saved passwords). Readable by any program running as you. |
| `logs\error.log` | Unexpected errors, local only. |

## Measure

```powershell
./tools/measure.ps1 -Exe .\out\Lilt\Lilt.exe                       # idle, window visible
./tools/measure.ps1 -Exe .\out\Lilt\Lilt.exe -Url https://en.wikipedia.org/wiki/Web_browser -Warm -Minimize -AfterMinimize 40   # efficiency mode
./tools/measure.ps1 -Browser edge -Exe "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
```

## License

GPL-3.0-or-later. See [LICENSE](LICENSE). Builds include [uBlock Origin Lite](https://github.com/uBlockOrigin/uBOL-home) (GPL-3.0), downloaded from its official release at build time and checked against a pinned SHA-256.
