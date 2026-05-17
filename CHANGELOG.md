# Changelog

All notable changes to Kivrio Chat are documented in this file.

## [Kivrio Chat 2026.5.17] - 2026-05-17

### Added
- Added real sidebar search across conversation history and folders with filters.
- Added PDF attachment text extraction through local PdfPig libraries.
- Added a backend safety net runner covering C# backend tests, PDF extraction, and fast Node regressions.

### Changed
- Hardened attachment validation and multipart upload parsing.
- Kept third-party payloads and local authentication data out of the source repository.
- Kept the `v2026.5.10` logout/reconnect pipeline for direct reconnection.

## [Kivrio Chat 2026.5.10] - 2026-05-10

### Added
- Added targeted automated tests for server security, upload cleanup, persistence recovery, Ollama abort handling, and frontend error UX.
- Added operational documentation for backup, restoration, dependency inventory, and structured logging.

### Changed
- Hardened the local C# server with origin checks, security headers, login throttling, request size limits, strict attachment validation, and safer static/attachment path handling.
- Improved local JSON persistence with atomic writes, backups, schema versioning, migration backups, and recovery from a valid `.bak` store.
- Replaced technical frontend errors with clearer user-facing messages and non-blocking toasts.

### Removed
- Removed the obsolete Codex bridge from Kivrio Chat.

## [Unreleased] - 2026-05-01

### Changed
- Reframed the active documentation around Kivrio Chat instead of the old Kivrio release history.
- Kept the existing chat UX while restoring local session authentication in the autonomous C# server.
- Kept the current Ollama-facing frontend as the chat integration surface.

### Removed
- Removed obsolete active documentation references to old Kivrio release notes.
- Removed obsolete local runtimes and legacy math pipeline dependencies from the project tree in the previous cleanup phase.
