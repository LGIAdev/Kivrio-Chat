## What's Changed

- Published Kivrio Chat as its own standalone application and GitHub project.
- Kept Kivrio Chat separate from Kivrio and Kivrio Agent UI, with its own launcher, local port range, data store, and release history.
- Removed the obsolete Codex bridge from Kivrio Chat.
- Hardened local API security: origin checks, attachment traversal protection, MIME and upload limits, request size limits, security headers, and login throttling.
- Improved local persistence with atomic JSON writes, backups, schema versioning, migration backups, and recovery from a valid `.bak` store.
- Added structured server logs that avoid request bodies, cookies, authorization headers, conversation content, attachment contents, and full local filesystem paths.
- Improved frontend error handling with user-friendly messages and non-blocking toasts.
- Added targeted automated tests for security, uploads, persistence, Ollama abort behavior, and error UX.
- Updated operational documentation for backup, restoration, dependency inventory, and logging.
- Updated the visible README version to `Kivrio Chat 2026.5.10`.

## Full Changelog

https://github.com/LGIAdev/Kivrio-Chat/compare/v2026.5.9...v2026.5.10
