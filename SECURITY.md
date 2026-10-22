# Security

Lilt is a personal browser project, public so anyone can read and build it. To report a security problem, open a private
security advisory on GitHub (Security → Report a vulnerability) rather than a public issue. Please include the version
(Lilt's About or the release name), what you did and what happened.

What Lilt does to keep you safe, and where to look:

- Developer switches (`--run`, `--browser-args`, `--user-data`, `--user-agent`, `--install-extension`, `--startup-report`)
  only work when the `LILT_DEV` environment variable is `1`, which only the CI build sets. Other launches can't change the engine.
- Chrome extensions are installed only after their CRX3 signature checks out against the extension ID.
- Theme files, bookmarks, history and sessions are parsed defensively; bad values fall back to defaults.
- Font names, page CSS and page script values are cleaned or JSON-encoded before they reach a page.
- Lilt keeps no password list of its own. Passwords you let the browser save live in WebView2's profile on your PC, and the
  Wipe option deletes that profile.
