## What's Changed

- Completed the targeted code review, robustness tests, and security tests for the Web Search implementation added in `Kivrio Chat 2026.5.12`.
- Updated the SearXNG runtime launch smokecheck so it purges temporary runtime state after stopping the managed process.
- Strengthened the Web Search runtime denylist to keep development and documentation-only artifacts out of the future runtime bundle.
- Verified that Web Search still fails closed when search is unavailable, preventing the model from answering without retrieved sources.
- Verified that conversation deletion leaves the active store and recovery backup clean from the user's perspective.
- Confirmed that KaTeX assets, the Python runtime, and SearXNG vendor payloads remain local-only and are not tracked as source repository content.
- Updated `README.md` for `Kivrio Chat 2026.5.12.1`.

## Full Changelog

https://github.com/LGIAdev/Kivrio-Chat/compare/v2026.5.12...v2026.5.12.1
