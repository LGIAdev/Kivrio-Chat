## What's Changed

- Turned the in-progress white square in the composer send button into a real Stop control: clicking it now aborts the current model response and restores the arrow immediately.
- Kept the existing partial-response behavior after Stop, without adding automatic continuation or changing conversation storage.
- Shifted the conversation action menu in the sidebar to the right with a compact width, keeping the existing `Modifier`, `Deplacer vers un dossier`, and `Supprimer` behavior unchanged.
- Removed the local authentication record from the working tree so the user password is not kept locally or tracked.
- Confirmed the active local conversation database and backup are valid and empty before release.
- Confirmed third-party runtime/vendor payloads remain out of GitHub; only placeholder files are tracked for local vendor/runtime directories.
- Updated `README.md` for `Kivrio Chat 2026.5.23`.

## Full Changelog

https://github.com/LGIAdev/Kivrio-Chat/compare/v2026.5.21...v2026.5.23
