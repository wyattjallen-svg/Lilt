# Prototype report

Scope: the minimal prototype from `docs/architecture.md` section 11, plus the review changes (pop-ups with `window.opener`, downloads panel, certificate errors, autofill settings, page color scheme, user CSS/JS, local new-tab page, spike switches). It is not a complete browser: one page view, no tabs, no content blocking, no settings UI.

## Build and verification

Build tested: commit `2035846`, [CI run](https://github.com/wyattjallen-svg/Lilt/actions/runs/38674868465), release `prototype-build`. Output: 214 MB unpacked, 458 files, 80 MB zipped (self-contained .NET and Windows App SDK; `Microsoft.Windows.SDK.NET.dll` alone is 56 MB).

| Check | Where | Result |
|---|---|---|
| Core unit tests (35, including generated JS run under Node) | Linux, .NET 10.0.112 | Pass |
| Core unit tests | Windows CI | Pass (JS checks skipped: no Node path configured) |
| Self-contained ReadyToRun publish | Windows CI (Windows Server 2025) | Pass |
| Launch to new-tab page (Dusk, pinned left sidebar) | Windows CI | Pass; first page loaded; screenshot checked |
| Launch to example.com (Dusk) | Windows CI | Pass; site rendered dark via `pages.colorScheme` |
| Launch to example.com with `paper.json` (light, auto-hide right sidebar) | Windows CI | Pass |
| Unexpected errors (`error.log`) during launches | Windows CI | None |
| Interactive testing | Your PC | Checklist below |

Bugs CI caught before this build: a crash when the address bar took focus (a CsWinRT delegate-marshalling rule; fixed with a lambda) and the new-tab page returning `ERR_ACCESS_DENIED` (virtual host mapping doesn't serve `index.html` for `/`; fixed). CI now fails the build if Lilt exits early, logs an error, or its first page doesn't load.

## Measured idle resource use

**These are CI numbers, not your PC.** GitHub's Windows Server 2025 VM: 2 vCPUs (AMD EPYC 7763), 8 GB, no GPU (software rendering), High performance power plan, WebView2 runtime 153.0.4234.48. `tools/measure.ps1`, fresh profile per run, `about:blank`, 3 runs, 20 s settle, CPU over 30 s, whole process tree.

| Scenario | Private bytes (median, range) | Working set (median, range) | Processes | CPU, % of one core | Start → window shown | Start → first page |
|---|---|---|---|---|---|---|
| Window visible | 143.9 MB (143.8–144.0) | 398.0 MB (396.4–400.6) | 7 | 0.2% (0.1–0.4) | 471 ms (446–559) | 1.34 s (1.26–1.56) |
| Minimized (10 s after minimizing) | 147.3 MB (142.9–147.3) | 398.5 MB (394.6–402.0) | 7 | 0.3% (0.2–0.5) | — | — |

Per process (visible, medians):

| Process | Count | Working set | Private |
|---|---|---|---|
| Lilt.exe (WinUI shell) | 1 | 126.5 MB | 43.1 MB |
| WebView2 browser process | 1 | 115.2 MB | 41.8 MB |
| Renderer (about:blank) | 1 | 50.7 MB | 22.1 MB |
| Utility (network, storage) | 2 | 52.7 MB | 19.7 MB |
| GPU process | 1 | 35.2 MB | 14.0 MB |
| Crashpad handler | 1 | 17.8 MB | 3.4 MB |

Reading these:

- Private bytes is the honest total. Summed working set double-counts DLL pages shared between processes, so ~400 MB overstates real use.
- The WinUI shell is about 30% of private memory; the rest is WebView2's own processes, which any WebView2 or Chromium browser pays.
- Minimizing made no measurable difference at idle on a blank page. Whether it matters with real pages is scenario 4 with tabs.
- Idle CPU is near zero: no timers or polling in the shell.
- Startup on this VM: environment creation starts with the window, which shows in under half a second; the WebView2 is ready about 1.1 s in.

Limitations of these numbers: a VM without a GPU and with Windows Server rather than Windows 11; three runs; processes that exited during the sample window aren't counted in CPU; Mica rendering wasn't checked on the runner. Comparisons with Edge, Firefox and Helium weren't run: they need your PC (`tools/measure.ps1 -Browser edge|firefox|helium`), and the runner's numbers wouldn't transfer to your hardware anyway.

## Testing this build (spike 1 checklist)

Unzip `Lilt-win-x64.zip` (or all three `part` zips into one folder) and run `Lilt.exe`. Note anything that fails; the decision on Google sign-in comes first.

**Go/no-go: sign-in**
- [ ] Sign in at accounts.google.com, then open Gmail and YouTube signed in.
- [ ] If Google refuses ("This browser or app may not be secure"), close Lilt and retry with a current Edge user agent: `Lilt.exe --user-agent "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/<ver>.0.0.0 Safari/537.36 Edg/<ver>.0.0.0"`.
- [ ] Passkey sign-in with Windows Hello (e.g. a Google or GitHub passkey), and a 2FA prompt.
- [ ] "Sign in with Google/Apple/GitHub" on a third-party site: the pop-up opens as a small Lilt window with its address shown, and closes itself after sign-in.
- [ ] A card payment that opens a 3-D Secure window, if you have one handy.
- [ ] In DevTools console (F12) on any site: `window.chrome.webview` is `undefined`.

**Composition hosting**
- [ ] Typing with an IME or emoji panel (Win+.) in a page text field.
- [ ] Dragging a file from Explorer into a page (e.g. an upload area).
- [ ] File upload dialog and "Save as" from a page; a download appears in the sidebar's Downloads section.
- [ ] Right-click context menu on a page, a link, an image.
- [ ] Save a password when prompted, restart Lilt, and check the autofill dropdown offers it.
- [ ] Ctrl+mouse wheel zoom on one site, navigate elsewhere and back: is the zoom remembered per site?

**Shell**
- [ ] Sidebar: dock left, right, top, bottom (sidebar buttons); unpin (pin button) and reveal it by pushing the pointer to that edge; it hides shortly after the pointer leaves; Escape hides it; Ctrl+B toggles.
- [ ] Keyboard with focus in the page: do Ctrl+L, Alt+D, F6 or Ctrl+B do anything? (Expected: no. Tab out of the page and they work.)
- [ ] Edit `theme.json` (pencil button) and save: colors and sizes change live. Break the JSON on purpose: a notice explains and the old theme stays.
- [ ] Copy `paper.json` over `theme.json`: light theme, right-hand auto-hide sidebar.
- [ ] Put `example.com.css` with `body { outline: 4px solid red }` in the user folder (`</>` button), reload example.com.
- [ ] `Lilt.exe --inprivate`: title says InPrivate; nothing persists after closing.
- [ ] HTTPS upgrade flag test: `Lilt.exe --browser-args "--enable-features=HttpsUpgrades"` then type `http://example.com`; does it end up on https?
- [ ] Memory button in the sidebar gives a number.

## Known limitations

- One page view; `target=_blank` links open in a pop-up window until tabs exist.
- Keyboard shortcuts don't work while focus is inside the page (section 4.5 of the architecture doc).
- No content blocking beyond WebView2's Balanced tracking prevention.
- Permission requests are always denied (camera, microphone, location, notifications...), with a notice.
- Links to other apps (`mailto:`, `ms-settings:` ...) are blocked, with a notice.
- No per-site zoom control, settings UI, history or bookmarks.
- Sidebar placement changed in the UI isn't saved between runs; the theme file sets the starting layout.
- Windows high contrast is read at startup and on theme reload, not live.
- If the WebView2 browser process dies, Lilt offers a restart rather than recovering in place.

## Dependency audit

| Dependency | Version | Why | License | Removable? |
|---|---|---|---|---|
| Microsoft.WindowsAppSDK.WinUI (+ Base, Foundation, InteractiveExperiences, Microsoft.Windows.SDK.BuildTools, .BuildTools.MSIX, transitively) | 2.3.9 | WinUI 3 window, controls, Mica, title bar | Microsoft Software License Terms (redistributable) | Only by moving to WPF |
| Microsoft.Web.WebView2 | 1.0.4258.31 | WebView2 SDK, newer than WinUI's minimum | BSD-3-Clause-style | No |
| .NET 10 runtime (bundled) | 10.0.x | Runtime, System.Text.Json | MIT | No |

No other packages. Tests use a 60-line runner instead of a test framework.
