# Prototype report (v0.3)

Scope: v0.2 (tabs, sessions, bookmarks and import, home tab, glass, Customize panel, uBlock Origin Lite, Chrome Web Store extensions) plus v0.3: the design language from your sheet, every toolbar item, sidebar section and home element movable and removable, and efficiency mode. Architecture sections 6.5, 6.6, 8.0 and 14 describe them.

## Build and verification

Build tested: commit `25ef4d3`, [CI run](https://github.com/wyattjallen-svg/Lilt/actions/runs/39448993185), release `prototype-build`. Output: 246.5 MB unpacked, 1,392 files, 89.5 MB zipped (uBlock Origin Lite adds about 33 MB unpacked).

| Check | Where | Result |
|---|---|---|
| Core unit tests (50) | Linux and Windows CI | Pass |
| Self-contained ReadyToRun publish | Windows CI (Windows Server 2025) | Pass |
| Launches: home tab (Minimal), example.com, Light theme page and home, Glass home, ad-block tester, Proton Pass install, keyboard and session, efficiency mode | Windows CI | All load their first page; no crashes; no `error.log` |
| uBlock Origin Lite installs on first start and is on | Windows CI | Pass; [adblock-tester.com](https://adblock-tester.com/) scores 91/100 |
| Proton Pass installs from the Chrome Web Store (signature-checked) | Windows CI | Pass |
| Ctrl+T from the address box (XAML) and from inside a page (key shim); session saved on close | Windows CI | Pass (3 tabs restored) |
| Efficiency mode: paused behind a full-screen window and when minimized, page suspended both times, back to full in front | Windows CI | Pass |
| Glass theme selects Acrylic; Light and Minimal render; new icon in the taskbar | Windows CI | Pass (screenshots) |
| Interactive testing | Your PC | Checklist below |

Bugs CI caught in this round: the sidebar's restored tab started loading before the ad blocker was ready; the in-page shortcut shim needed its binding installed per document (Chromium only installs name-based bindings when `Runtime.enable` is on, which Lilt avoids because sites can detect it); WebView2 event senders aren't always the same object as the stored view; a crash when the window closed while efficiency mode read a closing view; a stuck measurement step (now time-limited).

## Measured resource use

**CI numbers, not your PC.** GitHub's Windows Server 2025 VM: 2 vCPUs (AMD EPYC 7763), 8 GB, no GPU, WebView2 runtime 154.0.4258.62. `tools/measure.ps1`, 3 runs, 20 s settle, CPU over 30 s, whole process tree.

| Scenario | Private bytes (median) | Working set (median) | Processes | CPU, % of one core | Start → first page |
|---|---|---|---|---|---|
| `about:blank`, first start (installs uBlock Origin Lite) | 172.6 MB | 449.6 MB | 7 | 0.5% | 3.2 s |
| `about:blank`, no extensions | 145.6 MB | 415.7 MB | 7 | 0.3% | 1.9 s |
| `about:blank`, second start (ad blocker already installed) | 146.7 MB | 411.9 MB | 7 | 0.1% | 1.5 s |
| Wikipedia article, in front | 201.2 MB | 500.4 MB | 7 | 0.6% | 2.3 s |

Reading these:

- Once installed, uBlock Origin Lite costs about 1 to 4 MB at idle; the first start pays for unpacking it (the first page now waits at most 1.5 s for it, then the blocker joins and Lilt offers a reload).
- Lilt's own WinUI process is about 50 MB private; the rest is WebView2.
- Efficiency mode was verified by behavior on CI rather than by savings figures: behind a full-screen window and when minimized, Lilt reported "paused", its page was suspended, and every process's working set dropped as the low memory target paged memory out (the renderer and GPU process went from hundreds of MB to under 1 MB resident). It returned to normal within a moment of coming back to the front.
- CPU savings can't be measured meaningfully on this VM: it has no GPU, so an animated test page (WebGL) is rendered in software at nearly two full cores, which also starves Lilt's own timers. On your PC, `tools/measure.ps1 -Exe .\Lilt.exe -Url <a busy page> -Warm -Cover` measures Lilt behind a full-screen window; add `-Theme` with `"efficiency": false` for the comparison.

## Testing this build

Unzip all the `part` zips into one folder and run `Lilt.exe`. Your existing tabs, bookmarks and settings carry over. If your `theme.json` was never edited it switches to the new Minimal look; otherwise pick Minimal, Glass or Light under Customize → Look.

**Look and layout**
- [ ] Customize (palette button or Ctrl+Shift+,): try Minimal, Glass and Light; change the material, glass tint, accent and any color in the picker; corner radius, font, text size, toolbar height.
- [ ] Glass on your PC: with Glass or thin Acrylic the toolbar and sidebar should show a blurred desktop behind them (CI's VM can't render this). The Glass home tab shows hills; Home → Background → Pick an image uses your own.
- [ ] Layout tab: each preset (Compact, Research, Vertical tabs, Top tabs, Bottom bar); untick toolbar items and move others with the arrows; reorder or hide sidebar sections.
- [ ] Home tab: reorder or hide the logo, clock, search, tiles, hint and corner button; add a tile with +, remove one with its ×; visit a bookmarked site and its tile picks up the site icon.

**Tabs**
- [ ] Right-click a tab: pin (icon only, stays on top), mute, duplicate, bookmark, close others. A page with sound shows a speaker you can click to mute. Loading shows a spinner; a failed load turns the tab red.
- [ ] Ctrl+T, Ctrl+W, Ctrl+Tab, Ctrl+1–9, Ctrl+L, Ctrl+D and Alt+F work with the focus inside a page as well as in the toolbar.
- [ ] Close and reopen Lilt: tabs (including pinned) come back.

**Efficiency mode**
- [ ] Customize → Power shows the status. Start a game (or any full-screen app) with Lilt open behind it: after about 10 seconds Task Manager should show Lilt and its WebView2 processes with the efficiency-mode leaf icon, and Lilt's CPU near zero. Come back: everything resumes, nothing reloads.
- [ ] Play music or a video in a tab, then switch to the game: Lilt stays at full speed so playback doesn't stutter.
- [ ] Minimize Lilt for a minute and restore it: pages come back where they were.
- [ ] Optional: compare `tools/measure.ps1 -Url <a page> -Warm -Minimize -AfterMinimize 40` against Edge or Helium.

**Carried over from v0.2 (still worth confirming)**
- [ ] Google sign-in (accounts.google.com, Gmail, YouTube), passkeys, OAuth pop-ups.
- [ ] Bookmarks import from Chrome/Helium/Edge or an HTML/CSV file; Proton Pass (Extensions button → Add Proton Pass) fills a login form.
- [ ] uBlock Origin Lite: adblock-tester.com score; its dashboard opens from the menu.

## Known limitations

- The toolbar stays at the top: it's also the window's title bar. Everything on it can be reordered or hidden.
- Glass rendering and efficiency-mode's Task Manager leaf can't be checked on CI's VM; screenshots there show the solid fallback.
- Pages hidden for power (minimized or behind a full-screen app) take a moment to repaint when you come back.
- Extensions' popups don't know the current tab (WebView2 limit); per-site ad-blocking modes are set in uBlock Origin Lite's dashboard.
- Google Password Manager can't run outside Chrome; Proton Pass or WebView2's own saving are the options.
- Permission prompts (camera, location...) are still denied with a notice.

## Dependency audit

| Dependency | Version | Why | License | Removable? |
|---|---|---|---|---|
| Microsoft.WindowsAppSDK.WinUI (+ Base, Foundation, InteractiveExperiences, Windows SDK build tools, transitively) | 2.3.9 | WinUI 3 window, controls, Mica/Acrylic, title bar | Microsoft Software License Terms (redistributable) | Only by moving to WPF |
| Microsoft.Web.WebView2 | 1.0.4258.31 | WebView2 SDK | BSD-3-Clause-style | No |
| .NET 10 runtime (bundled) | 10.0.x | Runtime, System.Text.Json | MIT | No |
| uBlock Origin Lite | 2026.1018.2229 | Ad and tracker blocking, downloaded at build time and SHA-256 pinned | GPL-3.0 | Yes |

No other packages. The icon is drawn by `tools/make-icon.py` (Pillow, build-time only, not shipped).
