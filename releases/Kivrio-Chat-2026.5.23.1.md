## What's Changed

- Added optional local voice dictation for the Micro button through a locally configured `whisper.cpp` executable and Whisper model.
- Added the `/api/voice/transcribe` local backend endpoint with WAV validation, request size limits, timeout handling, and automatic cleanup of temporary dictation audio.
- Updated the Micro button from a placeholder to a dictation control that records audio, transcribes it locally, and inserts the recognized text into the prompt without sending it automatically.
- Kept the logout/reconnect UI safety test aligned with the existing i18n language-change listener.
- Added local `integrations/whisper/` documentation, placeholders, and example configuration while keeping third-party binaries and models out of the source repository.
- Removed the local authentication record from the working tree so the user password is not kept locally or tracked.
- Confirmed the active local conversation database and backup are valid and empty before release.
- Confirmed `whisper.cpp`, Whisper model files, SearXNG, KaTeX, Python runtime files, and other third-party payloads remain out of GitHub.
- Updated `README.md` for `Kivrio Chat 2026.5.23.1`.

## Full Changelog

https://github.com/LGIAdev/Kivrio-Chat/compare/v2026.5.23...v2026.5.23.1
