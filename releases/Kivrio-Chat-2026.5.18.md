## What's Changed

- Added a compact `Langue` selector in `Reglages`, directly under `Theme`, with `Francais` selected by default on first use.
- Added a lightweight local i18n layer with FR/EN dictionaries and persisted preference through `localStorage` key `kivrio.ui.language`.
- Internationalized static UI labels, placeholders, titles, `aria-label` values, model status text, and settings/prompt modals.
- Internationalized dynamic user-facing text, including guidance messages, empty states, upload feedback, auth prompts, Web Search messages, message editing actions, conversation menus, folder menus, confirmations, and error toasts.
- Kept the implementation dependency-free: no `i18next` package or third-party payload was added to the repository.
- Confirmed third-party runtime/vendor payloads remain out of GitHub; only placeholder files are tracked for local vendor/runtime directories.
- Removed the local authentication record from the working tree so the user password is not kept locally or tracked.
- Updated `README.md` for `Kivrio Chat 2026.5.18`.

## Full Changelog

https://github.com/LGIAdev/Kivrio-Chat/compare/v2026.5.17...v2026.5.18
