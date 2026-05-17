## What's Changed

- Added real sidebar search across conversation history and folders, with `Tout`, `Historique`, and `Dossiers` filters plus a compact `Ctrl K` shortcut.
- Added PDF attachment text extraction through local PdfPig libraries, including backend extraction, frontend prompt integration, PDF attachment labels, and focused regression tests.
- Hardened attachment handling with signature validation for supported images and PDFs, safer filename handling, active HTML rejection, multipart boundary validation, and atomic attachment writes.
- Improved multipart upload parsing so boundary-like content inside files is preserved and UTF-8 `filename*` values are decoded safely.
- Added a backend safety net runner that compiles and runs the C# backend tests, PDF extraction tests, and fast Node regression tests.
- Kept the `v2026.5.10` logout/reconnect pipeline: `Deconnexion` logs out the session without stopping the local server, so direct reconnection remains available.
- Confirmed local-only third-party payloads remain out of GitHub, including KaTeX assets, Python runtime files, SearXNG vendor files, and local PdfPig DLLs under `server/lib/`.
- Removed the local authentication record from the working tree and kept `data/` ignored so user passwords and local conversation data are not tracked.
- Updated `README.md` for `Kivrio Chat 2026.5.17`.

## Full Changelog

https://github.com/LGIAdev/Kivrio-Chat/compare/v2026.5.12.1...v2026.5.17
