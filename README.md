# Kivrio Chat

![Status](https://img.shields.io/badge/status-WIP-blue)
![License](https://img.shields.io/badge/license-Apache--2.0%20%2F%20MPL--2.0-green)

Kivrio Chat is a local interface for using Codex CLI more comfortably with local models via [Ollama](https://ollama.com/).
It provides a desktop-style web UI with math rendering, local conversation history, and a fully local persistence layer.

Status: project under active development.
Version: Kivrio Chat 2026.5.9.

---

## Releases

- [Kivrio Chat 2026.5.9](releases/Kivrio-Chat-2026.5.9.md)

---

## Project status

Kivrio Chat is currently being rebuilt as a separate local interface.
Standalone release notes are now maintained for Kivrio Chat releases.

---

## Current features

- Local Ollama integration
- Dark/light theme support
- Markdown rendering with KaTeX
- Conversation history in the left sidebar
- Persistent local storage of conversations in a JSON file
- Rename and delete actions for conversation links
- Local autonomous backend serving both the UI and the API
- Local session authentication
- Direct file reading for supported multimodal models

---

## Local architecture

Kivrio Chat now runs as a local application made of:

- a local autonomous Windows server
- a local JSON conversation store
- a browser UI served from the same local server
- local Ollama models running outside Kivrio Chat
- direct file reading for supported multimodal models

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

### Authentication

Kivrio Chat currently runs as a local-only interface on `127.0.0.1`.
The autonomous backend protects local API routes with session-based authentication.

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

## Project structure

- `index.html`: main UI
- `js/`: frontend logic
- `server/`: local API server source
- `css/`: styles
- `bin/kivrio-chat-server.exe`: compiled local server, generated on demand or during packaging
- `data/kivrio-chat.json`: local conversation store

---

## Roadmap

- [x] Basic UI with Ollama integration
- [x] Markdown + KaTeX rendering
- [x] Local conversation history
- [x] Local JSON persistence
- [x] Sidebar rename/delete actions
- [x] File uploads for supported multimodal models
- [x] Local session authentication
- [ ] Codex CLI local bridge
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
