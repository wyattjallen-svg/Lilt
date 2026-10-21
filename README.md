# Lilt

A lighter way through the web: a personal, customizable Windows 11 browser built on WebView2 and WinUI 3.

Think of it as clay: the toolbar, sidebar, home tab, floating player, visualizer, split view and every shortcut are yours to move, hide, resize and restyle, from the Customize panel (Ctrl+Shift+,), the command palette (Ctrl+K) or one `theme.json` file.

v0.8: sleeping tabs (a tab you haven't looked at for an hour, or sooner when memory runs short, closes its page and uses nothing until you open it), a view of what's using memory, more power settings, a coded "wipe everything and start fresh", and a window that opens where you left it. v0.7: a panel beside the page for any tab (Spotify, YouTube, a chat), with the visualizer and player docked under it; a toolbar and sidebar that take on each site's color; and storage that stays small without signing you out. v0.6: workspaces, a bookmark manager, see-through websites, mouse gestures, site permissions and tracker levels, whole-page screenshots, a window that lifts while you drag it, your own theme presets, and two new themes (Midnight, Paper) and home backgrounds (Aurora, Waves). v0.5 added address-bar completion and history, your search engine, and links opened behind with Ctrl/middle-click.

v0.4: a now-playing player with artwork and controls, a music visualizer fed by Lilt's own sound, picture in picture and in-window video pop-out, split view, link peek (Shift+click), a command palette, remappable shortcuts, and a quieter efficiency mode (pages stop drawing behind other windows; memory handed back while hidden). Plus everything from v0.3: the Lilt design language (Minimal, Glass and Light), tabs with saved sessions and pinning, bookmarks and import, uBlock Origin Lite built in, Proton Pass one click away.

## Get it

Download the latest release from [Releases](https://github.com/wyattjallen-svg/Lilt/releases/latest), unzip it anywhere and run `Lilt.exe` (Windows 11 x64). It is self-contained (no .NET or Windows App SDK install needed) and uses the WebView2 Runtime that ships with Windows 11.

| Keys | |
|---|---|
| Ctrl+K | Command palette: commands, tabs, bookmarks, presets, settings |
| Ctrl+L, type | Address bar: completes sites you visit; ↑↓ to pick, Shift+Delete forgets a page, Ctrl+Enter adds www/.com |
| Ctrl+click / middle-click a link | Open it in a tab behind this one |
| Ctrl+Shift+S | Split view |
| Ctrl+Shift+E | Next workspace (click "Tabs" above the tab list to make one) |
| F4 | Show or hide the panel (drag a tab into it to keep it there) |
| Ctrl+Shift+O | Bookmark manager |
| Ctrl+Shift+X | Screenshot of the whole page |
| Hold right button and draw | ← back, → forward, ↑↓ reload, ↓ new tab, ↓→ close tab |
| Shift+click a link | Peek |
| Ctrl+Shift+M / Ctrl+Shift+U | Now-playing player / visualizer |
| Alt+P | Pop the playing video out |
| Ctrl+Shift+, | Customize (Keys tab: change any shortcut) |

- Architecture and decisions: [docs/architecture.md](docs/architecture.md)
- Release report (CI checks, measurements, test checklist, limitations): [docs/prototype-report.md](docs/prototype-report.md)

Development builds: CI publishes every push to `prototype` as the **prototype-build** pre-release. A new `<Version>` in `src/Lilt.App/Lilt.App.csproj` reaching `main` (or a `v*` tag) becomes a release, with notes from `docs/releases/v<version>.md`, once the build passes every check.

```
Lilt.exe [url] [--url <url>] [--theme <file>] [--user-data <folder>] [--inprivate]
         [--install-extension <Web Store link or id>] [--no-extensions]
         [--user-agent "<ua>"] [--browser-args "<chromium switches>"] [--startup-report <file>]
         [--run "<command>; wait <ms>; open <url>; peek <url>; state; ..."]
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
| `theme.json` | Everything you can customize: colors, materials, sizes, the order and visibility of toolbar items, sidebar sections and home elements, where the player, visualizer and popped-out videos float, split view, shortcuts and power settings. The Customize panel writes it; you can also edit and save it and the window updates live. `theme.schema.json` beside it gives editor completion; `minimal.json`, `glass.json`, `light.json`, `midnight.json` and `paper.json` are the presets. |
| `themes\` | Presets you saved (Customize → Look → Save as preset). |
| `session.json` | Open tabs in every workspace, the tabs in the panel, the split-view pair and where the window was, restored on the next start. |
| `wipe.json` | Only while a wipe is waiting for Lilt to restart (Customize → Privacy → Wipe everything). |
| `history.json` | Pages you've visited, for address-bar suggestions (`pages.history`; never in InPrivate; Menu → Clear history). |
| `bookmarks.json` | Bookmarks. A bookmark folder named `Home` picks the home tab's tiles. |
| `user\` | Per-site CSS and JS: `example.com.css`, `github.com.js`, `all.css`. Reloaded on save. |
| `newtab\` | The home tab. Lilt refreshes its own files on start; put changes in `custom.css` and `custom.js`. A background image you pick is copied here; `icons\` holds favicons for your tiles. |
| `Extensions\` | Installed extensions (uBlock Origin Lite, anything from the Chrome Web Store). |
| `WebView2\` | Browser profile (cookies, cache, history, saved passwords). Readable by any program running as you. Kept small by `storage` in the theme: a cache limit, and sites you stop using tidied up without losing sign-ins. |
| `logs\error.log` | Unexpected errors, local only. |

## Measure

```powershell
./tools/measure.ps1 -Exe .\out\Lilt\Lilt.exe                       # idle, window visible
./tools/measure.ps1 -Exe .\out\Lilt\Lilt.exe -Url https://en.wikipedia.org/wiki/Web_browser -Warm -Minimize -AfterMinimize 40   # efficiency mode
./tools/measure.ps1 -Exe .\out\Lilt\Lilt.exe -Url https://newtab.lilt.example/bench.html -Warm -Occlude    # animated page behind another window
./tools/measure.ps1 -Browser edge -Exe "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
```

## License

GPL-3.0-or-later. See [LICENSE](LICENSE). Builds include [uBlock Origin Lite](https://github.com/uBlockOrigin/uBOL-home) (GPL-3.0), downloaded from its official release at build time and checked against a pinned SHA-256.
