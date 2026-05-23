# Kivrio Chat

![Status](https://img.shields.io/badge/status-WIP-blue)
![License](https://img.shields.io/badge/license-Apache--2.0%20%2F%20MPL--2.0-green)

Kivrio Chat is a local chat interface for working with local AI models via [Ollama](https://ollama.com/).
It provides a desktop-style web UI with Markdown rendering, file-aware conversations, local session authentication, and a fully local JSON persistence layer.

Status: project under active development.
Version: Kivrio Chat 2026.5.23.1.

---

## Releases

- [Kivrio Chat 2026.5.23.1](releases/Kivrio-Chat-2026.5.23.1.md)
- [Kivrio Chat 2026.5.23](releases/Kivrio-Chat-2026.5.23.md)
- [Kivrio Chat 2026.5.21](releases/Kivrio-Chat-2026.5.21.md)
- [Kivrio Chat 2026.5.18](releases/Kivrio-Chat-2026.5.18.md)
- [Kivrio Chat 2026.5.17](releases/Kivrio-Chat-2026.5.17.md)
- [Kivrio Chat 2026.5.12.1](releases/Kivrio-Chat-2026.5.12.1.md)
- [Kivrio Chat 2026.5.12](releases/Kivrio-Chat-2026.5.12.md)
- [Kivrio Chat 2026.5.10](releases/Kivrio-Chat-2026.5.10.md)

---

## Project status

Kivrio Chat is maintained as a standalone application and repository.
It is separate from Kivrio and Kivrio Agent UI, with its own launcher, local port range, data store, and release history.

---

## Current features

- Local Ollama integration
- Dark/light theme support
- Markdown rendering with KaTeX
- Conversation history in the left sidebar
- Real sidebar search across conversation history and folders
- Persistent local storage of conversations in a JSON file
- Rename and delete actions for conversation links
- Local Windows backend serving both the UI and the API
- Local session authentication
- Direct file reading for supported multimodal models
- PDF text extraction for uploaded PDF attachments when local PdfPig libraries are present
- Local Web Search integration through a managed SearXNG runtime when present
- Optional local voice dictation through `whisper.cpp` when configured locally

---

## Local architecture

Kivrio Chat now runs as a local application made of:

- a local Windows server
- a local JSON conversation store
- a browser UI served from the same local server
- local Ollama models running outside Kivrio Chat
- direct file reading for supported multimodal models
- optional local Web Search runtime files that are kept out of the source repository
- optional local PDF extraction libraries kept out of the source repository
- optional local `whisper.cpp` binaries and Whisper models kept out of the source repository

Conversation data is stored locally in:

`data/kivrio-chat.json`

No cloud database is used for conversation history.

---

## Quickstart

### Windows

Run:

```powershell
.\start-kivrio-chat.bat
```

Then open:

[http://127.0.0.1:8020/index.html](http://127.0.0.1:8020/index.html)

### Manual start

```powershell
cd "$env:USERPROFILE\Documents\Kivrio Chat"
.\bin\kivrio-chat-server.exe --root . --host 127.0.0.1 --port 8020
```

Then open:

[http://127.0.0.1:8020/index.html](http://127.0.0.1:8020/index.html)

Make sure Ollama is installed locally and running, for example on:

`http://127.0.0.1:11434`

For image files, Kivrio Chat keeps file upload support for compatible multimodal models.

### Optional voice dictation

Kivrio Chat can use a local `whisper.cpp` executable for the Micro button.

Third-party binaries and models are not included in this repository. Keep them local only:

- `integrations/whisper/bin/`
- `integrations/whisper/models/`

Copy `integrations/whisper/config.example.json` to `integrations/whisper/config.json`, then adjust the local executable and model paths. The Micro button inserts the recognized text into the prompt; it does not send the prompt automatically.

### Stopping Kivrio Chat

Use **Deconnexion** in the Kivrio Chat interface to leave the application cleanly.
This closes the local session, asks the local server to stop, and lets the backend clean up Web Search runtime state.

Closing only the browser tab closes the visible interface, but it does not guarantee that the local server process will stop.

### Backend safety net

Before backend changes, run the local safety net:

```powershell
node .\tests\run-backend-safety-net.mjs
```

It compiles and runs the C# backend tests with the local PdfPig dependencies, runs the fast API/client regression tests, and fails if a new Kivrio process is left running after the suite.

### Authentication

Kivrio Chat currently runs as a local-only interface on `127.0.0.1`.
The local backend protects local API routes with session-based authentication.

On first launch, the interface can create a local password stored in:

`data/auth.json`

Advanced local configuration keeps the Kivrio-compatible environment variables:

- `KIVRO_ADMIN_PASSWORD`
- `KIVRO_DISABLE_AUTH`
- `KIVRO_SESSION_TTL_SECONDS`
- `KIVRO_COOKIE_SECURE`

---

## Conversation history

Kivrio Chat stores conversations locally in a JSON store and rebuilds the left sidebar from that file at startup.

Supported behavior:

- reopen a saved conversation from the sidebar
- keep conversations after closing the interface
- keep conversations after a PC restart
- rename a conversation link
- delete a conversation link

Logging out of the interface no longer clears persistent conversation history.

---

## Operations and restoration

Kivrio Chat keeps its runtime state under the local `data/` directory.
Treat this directory as sensitive because it may contain conversations, uploaded files, and authentication data.

Important local paths:

- `data/kivrio-chat.json`: conversation store
- `data/auth.json`: local authentication record
- `data/uploads/`: conversation attachments
- `bin/kivrio-chat-server.exe`: generated local server binary
- `runtime/python/`: optional local Python runtime, not committed to Git
- `integrations/searxng/vendor/searxng/`: optional local SearXNG runtime, not committed to Git
- `assets/vendor/katex/`: optional local KaTeX payload, not committed to Git except its placeholder

Operational notes:

- `start-kivrio-chat.bat` recompiles the local server when the source file is newer than the generated binary.
- The launcher checks the local port range and uses `/api/health` to verify that the running server is Kivrio Chat.
- Keep Kivrio Chat bound to `127.0.0.1` unless you have reviewed authentication, cookies, and local network exposure.
- Avoid `KIVRO_DISABLE_AUTH` outside isolated local development.

Backup procedure:

1. Stop Kivrio Chat when possible.
2. Copy the whole `data/` directory, including `uploads/`.
3. Store the backup outside the served application directory.

Restore procedure:

1. Stop Kivrio Chat.
2. Replace the current `data/` directory with the backup copy.
3. Start Kivrio Chat again with `start-kivrio-chat.bat`.
4. Check `/api/health`, then reopen the UI and verify the conversation list.

Durability notes:

- JSON writes are atomic and keep the previous version as `<file>.bak`.
- The conversation store has an explicit schema version; legacy stores are migrated automatically with a `<file>.pre-migration-v<old>-to-v<new>-<id>.bak` backup.
- If `data/kivrio-chat.json` is unreadable but `data/kivrio-chat.json.bak` is valid, Kivrio Chat restores the active store from that backup.
- If a JSON file is unreadable, Kivrio Chat preserves the damaged file as `<file>.corrupt-<id>.bak` before continuing.
- To manually recover from a `.bak` or `.corrupt-*.bak` file, stop Kivrio Chat first, inspect the candidate file, then copy the chosen version back to `data/kivrio-chat.json` or `data/auth.json`.

---

## Project structure

- `index.html`: main UI
- `js/`: frontend logic
- `server/`: local API server source
- `css/`: styles
- `bin/kivrio-chat-server.exe`: compiled local server, generated on demand or during packaging
- `data/kivrio-chat.json`: local conversation store
- `integrations/searxng/`: Web Search integration code, packaging checks, and optional runtime mount points

---

## Roadmap

- [x] Basic UI with Ollama integration
- [x] Markdown + KaTeX rendering
- [x] Local conversation history
- [x] Local JSON persistence
- [x] Sidebar rename/delete actions
- [x] File uploads for supported multimodal models
- [x] Local session authentication
- [x] Local Web Search integration
- [ ] Voice input/output

---

## Contributing

Contributions are welcome.

Recommended flow:

1. Fork the project
2. Create a branch
3. Open a Pull Request

See also `CONTRIBUTING.md`.

---

## License

The source code is distributed under a dual license: Apache 2.0 / MPL 2.0.

See `LICENSE`.

---

## Trademark notice

Kivrio Chat is derived from the Kivrio interface. References to Kivrio are kept for attribution, compatibility, and migration context.

The name Kivrio, its logo, and its visual identity are trademarks of LG-IA ResearcherLab.

For trademark inquiries: `contact@lg-ia-researchlab.fr`
