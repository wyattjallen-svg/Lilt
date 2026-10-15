# Prototype report

Scope: the minimal prototype from `docs/architecture.md` section 11, plus the review changes (pop-ups with `window.opener`, downloads panel, certificate errors, autofill settings, page color scheme, user CSS/JS, local new-tab page, spike switches). It is not a complete browser: one page view, no tabs, no content blocking, no settings UI.

## Build and verification

| Check | Where | Result |
|---|---|---|
| Core unit tests (35, including generated JS run under Node) | Linux, .NET 10.0.112 | Pass |
| Core unit tests | Windows CI | _pending_ |
| Self-contained ReadyToRun publish | Windows CI (windows-2025) | _pending_ |
| Launch, startup report, screenshots | Windows CI | _pending_ |
| Idle measurements | Windows CI | _pending_ |
| Interactive testing | Your PC | Checklist below |

## Measured idle resource use

_pending CI_

## Testing this build (spike 1 checklist)

Unzip `Lilt-win-x64.zip` and run `Lilt.exe`. Note anything that fails; the decision on Google sign-in comes first.

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
