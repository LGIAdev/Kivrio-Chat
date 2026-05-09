## What's Changed

- Published Kivrio Chat as its own standalone application and GitHub project.
- Renamed the local app identity, launchers, server source, installer scripts, icon, executable, and JSON store to use `Kivrio Chat` / `kivrio-chat`.
- Isolated Kivrio Chat on its own local port range (`8020-8029`) to avoid conflicts with Kivrio and Kivrio Agent UI.
- Added an application identity to `/api/health` so the launcher can verify that it is talking to Kivrio Chat before opening the browser.
- Reworked the Windows launcher to avoid slow HTTP port scans and keep startup responsive.
- Updated the visible README version to `Kivrio Chat 2026.5.9`.

## Full Changelog

https://github.com/LGIAdev/Kivrio-Chat/commits/v2026.5.9
