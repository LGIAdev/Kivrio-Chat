# Changelog

All notable changes to Kivrio Chat are documented in this file.

## [Kivrio Chat 2026.5.23.1] - 2026-05-23

### Added
- Added optional local voice dictation for the Micro button through a locally configured `whisper.cpp` executable and Whisper model.
- Added the `/api/voice/transcribe` local backend endpoint with WAV validation, request size limits, timeout handling, and automatic cleanup of temporary dictation audio.
- Added local `integrations/whisper/` documentation, placeholders, and example configuration while keeping third-party binaries and models out of the source repository.

### Changed
- Updated the Micro button from a placeholder to a dictation control that records audio, transcribes it locally, and inserts the recognized text into the prompt without sending it automatically.
- Kept the logout/reconnect UI safety test aligned with the existing i18n language-change listener.
- Updated `README.md` for `Kivrio Chat 2026.5.23.1`.

### Removed
- Removed the local authentication record from the working tree so the user password is not kept locally or tracked.
- Confirmed the active local conversation database and backup are valid and empty.
- Confirmed `whisper.cpp`, Whisper model files, SearXNG, KaTeX, Python runtime files, and other third-party payloads remain out of GitHub.

## [Kivrio Chat 2026.5.23] - 2026-05-23

### Changed
- Turned the in-progress white square in the composer send button into a real Stop control: clicking it now aborts the current model response and restores the arrow immediately.
- Kept the existing partial-response behavior after Stop, without adding automatic continuation or changing conversation storage.
- Shifted the conversation action menu in the sidebar to the right with a compact width, keeping the existing `Modifier`, `Deplacer vers un dossier`, and `Supprimer` behavior unchanged.
- Updated `README.md` for `Kivrio Chat 2026.5.23`.

### Removed
- Removed the local authentication record from the working tree so the user password is not kept locally or tracked.
- Confirmed the active local conversation database and backup are valid and empty.
- Confirmed third-party runtime/vendor payloads remain out of GitHub; only placeholder files are tracked for local vendor/runtime directories.

## [Kivrio Chat 2026.5.21] - 2026-05-21

### Changed
- Added a clear in-progress visual state for the composer send button: the blue arrow button now switches to a centered white square while the model is responding, then returns to the arrow when generation finishes.
- Moved the copy confirmation toast below the top-right model selector to avoid overlapping the selector.
- Added the small green status dot to the copy confirmation toast.
- Updated `README.md` for `Kivrio Chat 2026.5.21`.

### Removed
- Removed the local authentication record from the working tree so the user password is not kept locally or tracked.
- Removed an obsolete local temporary conversation-store backup after confirming the active local database is valid and empty.
- Confirmed third-party runtime/vendor payloads remain out of GitHub; only placeholder files are tracked for local vendor/runtime directories.

## [Kivrio Chat 2026.5.18] - 2026-05-18

### Added
- Added a local FR/EN interface language selector in `Reglages`, with French as the first-use default.
- Added a lightweight centralized i18n layer with persisted language preference.

### Changed
- Internationalized the main UI, guidance messages, empty states, upload messages, auth prompts, Web Search messages, model status text, and conversation/folder menus.
- Updated `README.md` for `Kivrio Chat 2026.5.18`.

### Removed
- Removed the local authentication record from the working tree so the user password is not kept locally or tracked.
- Kept third-party payloads out of GitHub; only placeholder files remain tracked for local vendor/runtime folders.

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
