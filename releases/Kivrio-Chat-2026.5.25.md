## What's Changed

- Hardened Markdown/KaTeX rendering by escaping preserved math tokens before reinserting them into rendered message HTML.
- Made the managed SearXNG launcher timeout effective by collecting process output asynchronously instead of blocking before timeout handling.
- Stabilized the immediate Stop flow after sending: if generation is stopped before the user message is persisted, the transient user bubble is removed and the prompt input plus pending attachments are restored safely.
- Passed the local backend safety net covering persistence, upload limits, server security, PDF extraction, error UX, sidebar search, Ollama abort, Web Search prompt injection, Web Search sources, and PDF upload preparation.
- Passed local Web Search/SearXNG packaging and runtime smoke checks.
- Removed the local authentication record from the working tree so the user password is not kept locally or tracked.
- Confirmed the active local database files remain outside GitHub and temporary upload, voice, and SearXNG runtime directories are clean.
- Confirmed third-party runtime/vendor payloads, including Whisper, SearXNG, KaTeX, Python runtime files, local models, and PDF libraries, remain out of GitHub.
- Updated `README.md` for `Kivrio Chat 2026.5.25`.

## Full Changelog

https://github.com/LGIAdev/Kivrio-Chat/compare/v2026.5.23.2...v2026.5.25
