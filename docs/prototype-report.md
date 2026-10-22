# Release report (v0.8, with v0.4's numbers)

**v0.8**: sleeping tabs, what's using memory, solid-when-away and frame-rate settings, the coded wipe, window placement, recently closed tabs. Five new CI checks drive these: a wipe requested before start runs (bookmarks kept as asked, files that aren't Lilt's untouched), a background tab sleeps and wakes, the memory list fills in, and the wipe dialog opens and waits for its code. (At the time of writing GitHub had paused Actions for the account, so the run that checks them is listed on the release once it goes through.)

**v0.7**: the panel beside the page (any tab, with the visualizer and player docked under it), site colors for the toolbar and sidebar, and storage that stays small without signing you out. CI now runs 43 feature checks, all passing ([run](https://github.com/wyattjallen-svg/Lilt/actions/runs/39611352942)). The new ones:

| Check | Result |
|---|---|
| A playing tab moves into the panel and keeps playing (the tab list keeps a home tab) | Pass |
| The visualizer and player dock under it | Pass |
| The toolbar takes a page's theme-color (#3366CC at 45%, still readable) | Pass |
| Emptying caches leaves cookies alone (a cookie set before is still there after) | Pass |
| Hiding the panel keeps it playing, and the visualizer floats again | Pass |
| The panel is saved with the session | Pass |

Bug CI caught: moving the visualizer and player relied on `FrameworkElement.Parent`, which is still empty while the window is being built, so Lilt crashed at launch; the panel now asks its containers instead.

**v0.6** (after your second playtest): see-through websites, a bookmark manager, workspaces, mouse gestures, site permissions, tracker levels, whole-page screenshots, the window lift, your own presets, Midnight and Paper, Aurora and Waves, and the home "None" background fix. CI now runs 37 feature checks, all passing ([run](https://github.com/wyattjallen-svg/Lilt/actions/runs/39534250757)). The new ones:

| Check | Result |
|---|---|
| See-through websites: example.com's root keeps 72% of its color, its body goes clear | Pass |
| The bookmark manager opens with the bookmarks | Pass |
| A whole-page screenshot is saved | Pass |
| Workspaces keep their own tabs (Work: 1 of 2 tabs; back in Main the page is still there) and are saved with the session | Pass |
| The window lifts (motion.windowDrag) | Pass |
| A real right-button drag to the left goes back | Pass |
| A site asking for your location shows the permission bar | Pass |
| Protected content: Widevine and Clear Key available; PlayReady not on CI's server VM (info) | Pass |

Resource use is unchanged from v0.4 (below): workspaces you aren't in keep their pages suspended once idle, the window lift runs on the compositor, and see-through pages are styled once per page load.

**v0.5** (after your first playtest): password exports no longer become bookmarks; address-bar completion and suggestions; your search engine; Ctrl/middle-click open behind; bookmark editing with undo; tidier address bar and notices.

Scope: v0.4 adds the now-playing player, the visualizer, picture in picture with an in-window pop-out, split view, peek, the command palette, shortcuts from the theme and a quieter efficiency mode, on top of v0.3 (design language, everything movable and removable, efficiency mode) and v0.2 (tabs, sessions, bookmarks and import, home tab, glass, uBlock Origin Lite, Chrome Web Store extensions). Architecture sections 4.5, 6.7, 6.8, 8.0 and 15 describe the new parts.

## Build and verification

Release build: `v0.4.0` (the code of commit `4e2fb14` plus documentation and CI); that commit's [prototype run](https://github.com/wyattjallen-svg/Lilt/actions/runs/39496781108) passed all 24 feature checks and produced the measurements below. Every push builds a self-contained ReadyToRun x64 package on Windows CI (Windows Server 2025), runs the core tests, and launches the build through a set of scenarios with feature checks and screenshots; a new version reaching main publishes a release. Package: 89.7 MB zipped, 246.9 MB unpacked.

New features are driven by `--run` scripts (commands, waits and `state` snapshots of what's on screen) against a local test page that plays a looping tone with media-session metadata next to a silent animated video, so the checks don't depend on outside sites.

| Check | Result |
|---|---|
| Core unit tests (56 groups: theme parsing and writing, shortcuts, media info, spectrum, power rules, session, …) | Pass |
| All scenarios load their first page; no crashes; no `error.log`; every run closes within 15 s | Pass |
| Media reported from the page (title, artist, duration, playing) | Pass |
| Player stays out of the way while the playing tab is on screen; shows when it isn't | Pass |
| Visualizer draws and hears the sound (loopback where Windows can hear it; CI's VM has no audio device, so it switched to the tab's sound by itself) | Pass |
| Split view pairs two tabs; picture in picture opens Windows' floating video window (WebView2 supports it) and closes again | Pass |
| In-window pop-out: the tab floats in a frame over another tab, is dragged with real mouse input (the new place is saved to the theme) and goes back | Pass |
| Peek opens a link over the page; the command palette opens | Pass |
| A shortcut changed in the theme works inside pages; a removed one does nothing | Pass |
| Efficiency mode pauses behind a full-screen window and when minimized, suspends the page, returns to full in front | Pass |
| uBlock Origin Lite installs and is on; Proton Pass installs from the Chrome Web Store; Glass gets Acrylic; Ctrl+T in the toolbar and in pages; session saved | Pass |

What the engine on CI's VM reported (`--run probe`): picture in picture, document picture in picture, media session, `captureStream` and full screen all available; Windows lists Lilt's media as a session of `msedgewebview2.exe` with the page's title; process loopback works.

Bugs CI caught in this round: selecting the first tab at startup laid out pages before the media panel existed (crash at launch); a media element that started before the page helper arrived went unreported; screenshots raced the script; a minimized window that Windows left as the foreground window counted as "in front", so minimizing didn't pause Lilt; drags now follow the pointer at the content area instead of depending on pointer capture.

## Measured resource use

**CI numbers, not your PC.** GitHub's Windows Server 2025 VM: 2 vCPUs (AMD EPYC 7763), 8 GB, no GPU, WebView2 runtime 154.0.4258.62. `tools/measure.ps1`, 3 runs, 20 s settle, CPU over 30 s, whole process tree. The animated page is `bench.html` (400 moving circles on a canvas, bundled), software-rendered on this GPU-less VM.

| Scenario | Private bytes | Working set | CPU, % of one core | Power level |
|---|---|---|---|---|
| `about:blank`, first start (installs uBlock Origin Lite) | 173.1 MB | 448.0 MB | 0.3% | Full |
| `about:blank`, no extensions | 144.4 MB | 412.2 MB | 0.2% | Full |
| Animated page, in front | 264.5 MB | 562.4 MB | 9.7% (7–20% across runs) | Full |
| Animated page, behind another app's maximized window | 160.8 MB | 95.9 MB | 0.5% | Hidden |
| Animated page, minimized | 160.5 MB | 56.7 MB | 0.2% | Paused |
| Animated page, behind a full-screen window | 161.5 MB | 61.4 MB | 0.2% | Paused |
| Animated page, behind a full-screen window, efficiency mode off | 282.2 MB | 579.3 MB | 0.8% | (off) |

Reading these:

- Windows and Chromium already stop most drawing for a covered window (0.8% CPU with efficiency mode off). What efficiency mode adds is memory: Lilt's whole process tree drops from about 560–580 MB resident to 96 MB behind another window (Hidden) and about 60 MB minimized or behind a game (Paused), and 100–120 MB of private memory is released, so whatever is in front gets that RAM back. Everything returns when Lilt comes to the front; Hidden pages aren't suspended, so they repaint at once.
- CPU in front is the animated page itself (software rendering); Lilt's own UI process idles near zero, and its drawing (visualizer, player clock) stops whenever the window can't be seen.
- Minimized: the first measurement exposed that a window minimized with nothing else to activate stays the foreground window and never got deactivated, so Lilt didn't pause (555 MB working set). v0.4.0 checks on minimize itself and keeps its timer running whenever it isn't in front: 57 MB.

## Testing this build

Unzip `Lilt-v0.10.0-win-x64.zip` and run `Lilt.exe`. Tabs, bookmarks and settings from earlier builds carry over; your `theme.json` gains the new settings with their defaults.

**New in v0.10**
- [ ] Customize → Look → Interface size: try 125% and 80%. The toolbar, tabs, sidebar, Customize itself, menus (right-click a tab), the palette (Ctrl+K) and the home page should all change together, with nothing cut off. Move Text size too.
- [ ] Play music in a tab and switch to another tab: the player floats. Drag its bottom-right corner wider: the art, title and buttons grow. Dock it in the panel and drag the panel's edge: the same.
- [ ] Turn the visualizer on (Ctrl+Shift+U) while Spotify or YouTube plays: bars within a couple of seconds, and they stay.
- [ ] Type `about:logs` in the address bar. Try the filters, Copy (paste it somewhere) and Open folder. If Lilt crashes, this is the page to send me.
- [ ] Pop a YouTube video out (right-click its tab → Pop out the video) and drag its bar onto the panel: only the video shows there, and it keeps playing.

**New in v0.8**
- [ ] Open a few tabs, leave them for a while (or Customize → Power → "Put background tabs to sleep now"): they fade; Task Manager shows fewer Lilt processes. Click one: it reloads.
- [ ] Customize → Power → "What's using memory": tabs and their memory; Sleep frees one.
- [ ] Resize and move the window, maximize it, close Lilt and open it again: it comes back the same.
- [ ] Customize → Privacy → "Wipe everything and start fresh…": try a wrong code (the button stays off) and Cancel. When you do want a fresh start, enter the code; Lilt restarts empty (or with your bookmarks or look, if you ticked those).
- [ ] Menu → Recently closed.

**New in v0.7**
- [ ] Play something on Spotify or YouTube, then drag its tab to the right edge of the page (or right-click it → Move to the panel). It keeps playing in the panel; the visualizer shows under it. Browse, switch workspaces, press F4 to hide and show it.
- [ ] Put a second site in the panel and switch between them with the buttons along its top. Drag the panel's edge to resize it; its menu (⋯) moves it to the left or lets the visualizer float.
- [ ] Visit a few colorful sites (YouTube, GitHub, Spotify): the toolbar picks up each one's color. Customize → Look → Site colors.
- [ ] Customize → Privacy → Storage: the numbers, "Empty caches now" (you stay signed in everywhere), the cache limit and the tidy-up settings.

**New in v0.6**
- [ ] Customize → Home → Background: None. Pick a preset (Customize → Look), then another: the background stays None.
- [ ] Customize → Look → See-through websites on (with Mica or Acrylic). Browse a few sites; move "Page color kept". Right-click a tab → "Keep this site solid".
- [ ] Ctrl+Shift+O: search, sort, select several, Move to → New folder…, Undo from the notice, drag to reorder in "Your order", Delete.
- [ ] Click "Tabs" above the tab list → New workspace…; open tabs there; switch back and forth (Ctrl+Shift+E); right-click a tab → Move to workspace; play music in one and switch away. Restart: everything is where you left it.
- [ ] Hold the right button and draw left, right, down, down-then-right on a page. A plain right-click still opens the menu.
- [ ] A site that wants your camera or location (a video call site): the bar asks; Customize → Privacy lists the answer with "Ask again".
- [ ] Drag the window by its title bar: the content lifts slightly. Customize → Look → Motion turns it off.
- [ ] Ctrl+Shift+X on a long page: Pictures\Lilt has the whole page, and it's on the clipboard.
- [ ] Netflix (or another streaming service) plays.
- [ ] Customize → Look → Save as preset…; it appears with the others (right-click to delete). Try Midnight and Paper, and the Aurora and Waves backgrounds.

**Media**
- [ ] Play music on YouTube Music or Spotify, switch to another tab: the player shows title, artist and artwork; play/pause, previous/next, seek (click the bar) and mute work; the arrow goes back to the tab. Drag it somewhere else; restart Lilt and it's still there.
- [ ] The visualizer appears on music sites (Ctrl+Shift+U toggles it anywhere). Double-click it to change style. Customize → Media: bars, size, opacity, source, the music-site list.
- [ ] A YouTube video: Alt+P (or the player's pop-out button) opens Windows' floating video window; Alt+P again closes it. Set "Pop-out videos" to "Inside Lilt" and Alt+P floats the tab in a frame over the page you're on: drag it by its bar, resize from its corner, double-click the bar to go back.
- [ ] Media keys on your keyboard and Windows' volume flyout control what's playing.

**Layout**
- [ ] Ctrl+Shift+S splits the current tab with the previous one; drag the divider; click into either side; right-click a tab → Open in split view / Swap / Close split view. Customize → Layout: side by side or stacked, gap.
- [ ] Shift+click a link: it opens in a peek panel; Esc or a click outside closes it.
- [ ] Ctrl+K: type "glass", "split", a tab's title or a bookmark and press Enter.
- [ ] Customize → Keys: click a box and press a new combination; it works in pages and in the toolbar.

**Efficiency**
- [ ] Customize → Power shows the level. Cover Lilt with another app's window for 15 seconds: Task Manager shows Lilt's memory dropping; come back and it's instant. A full-screen game pauses it (leaf icon in Task Manager).
- [ ] While music plays in Lilt, it stays at full speed in the background.

## Known limitations

- The toolbar stays at the top: it's also the window's title bar. Everything on it can be reordered or hidden.
- Previous/next on sites Lilt doesn't know go through Windows' media controls, which only work if the site registers media session handlers.
- The visualizer hears Lilt's sound through Windows 11's loopback; on Windows 10 (not supported) or with no audio device it uses the tab's sound, which DRM-protected services (Spotify, Apple Music) don't allow.
- Peek opens links in a temporary view; "Open as tab" loads the page again.
- Glass rendering and the Task Manager leaf can't be checked on CI's VM; screenshots there show the solid fallback.
- Extensions' popups don't know the current tab (WebView2 limit).
- See-through websites work page by page: sites that paint their background in many small pieces, or with images, stay mostly solid.

## Dependency audit

| Dependency | Version | Why | License | Removable? |
|---|---|---|---|---|
| Microsoft.WindowsAppSDK.WinUI (+ Base, Foundation, InteractiveExperiences, Windows SDK build tools, transitively) | 2.3.9 | WinUI 3 window, controls, Mica/Acrylic, title bar | Microsoft Software License Terms (redistributable) | Only by moving to WPF |
| Microsoft.Web.WebView2 | 1.0.4258.31 | WebView2 SDK | BSD-3-Clause-style | No |
| .NET 10 runtime (bundled) | 10.0.x | Runtime, System.Text.Json | MIT | No |
| uBlock Origin Lite | 2026.1018.2229 | Ad and tracker blocking, downloaded at build time and SHA-256 pinned | GPL-3.0 | Yes |

No other packages. The icon is drawn by `tools/make-icon.py` (Pillow, build-time only, not shipped).
