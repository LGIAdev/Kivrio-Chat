## What's Changed

- Added the Web Search option in the existing `+` menu without creating a separate search page.
- Added the local `/api/web-search` backend path with guarded SearXNG integration, normalized result handling, and source metadata.
- Added Web Search prompt injection so the model answers from retrieved sources when search succeeds.
- Added citation storage and rendering for assistant messages with Web Search sources.
- Made Web Search fail closed: when search is unavailable, Kivrio Chat shows a clear assistant message instead of letting the model hallucinate.
- Added managed SearXNG lifecycle handling with stale PID cleanup, health checks, runtime purge, and shutdown cleanup.
- Added user-focused cleanup after deleting conversations or folders, including active store and recovery backup synchronization.
- Added local server shutdown through **Deconnexion**, and documented that closing only the browser tab does not guarantee server shutdown.
- Updated README.md for `Kivrio Chat 2026.5.12` and included the release documentation link.
- Kept third-party payloads out of the source repository: KaTeX assets, Python runtime files, and the SearXNG vendor runtime are local-only placeholders in GitHub.
- Added targeted tests for Web Search API behavior, prompt fail-closed behavior, source persistence, launcher stale PID handling, server security, and persistence cleanup.

## Full Changelog

https://github.com/LGIAdev/Kivrio-Chat/compare/v2026.5.10...v2026.5.12
