## What's Changed

- Added a clear in-progress visual state for the composer send button: the blue arrow button now switches to a centered white square while the model is responding, then returns to the arrow when generation finishes.
- Moved the copy confirmation toast below the top-right model selector to avoid overlapping the selector.
- Added the small green status dot to the copy confirmation toast.
- Removed the local authentication record from the working tree so the user password is not kept locally or tracked.
- Confirmed the active local conversation database is valid and empty before release.
- Confirmed third-party runtime/vendor payloads remain out of GitHub; only placeholder files are tracked for local vendor/runtime directories.
- Updated `README.md` for `Kivrio Chat 2026.5.21`.

## Full Changelog

https://github.com/LGIAdev/Kivrio-Chat/compare/v2026.5.18...v2026.5.21
