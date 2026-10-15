# Lilt

A personal, customizable Windows 11 browser built on WebView2 and WinUI 3. Early prototype: one page view, no tabs or ad blocking yet.

- Architecture and decisions: [docs/architecture.md](docs/architecture.md)
- Prototype results, test checklist and limitations: [docs/prototype-report.md](docs/prototype-report.md)

## Run a build

CI publishes every push to `prototype` as the **prototype-build** pre-release. Download `Lilt-win-x64.zip` (80 MB), unzip it anywhere and run `Lilt.exe`. It is self-contained (no .NET or Windows App SDK install needed) and uses the WebView2 Runtime that ships with Windows 11.

```
Lilt.exe [url] [--url <url>] [--theme <file>] [--user-data <folder>] [--inprivate]
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
| `theme.json` | Active theme. Edit and save; the window updates live. `theme.schema.json` beside it gives editor completion; `paper.json` is a light sample. |
| `user\` | Per-site CSS and JS: `example.com.css`, `github.com.js`, `all.css`. Reloaded on save. |
| `newtab\` | The start page (`index.html`). Edit freely. |
| `WebView2\` | Browser profile (cookies, cache, history, saved passwords). Readable by any program running as you. |
| `logs\error.log` | Unexpected errors, local only. |

## Measure

```powershell
./tools/measure.ps1 -Exe .\out\Lilt\Lilt.exe                       # idle, window visible
./tools/measure.ps1 -Exe .\out\Lilt\Lilt.exe -Minimize -Label minimized
./tools/measure.ps1 -Browser edge -Exe "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
```

## License

GPL-3.0-or-later. See [LICENSE](LICENSE).
