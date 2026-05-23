# Whisper integration

This folder documents the optional local voice dictation integration for Kivrio Chat.

Kivrio Chat does not vendor `whisper.cpp` binaries or Whisper models. Keep those third-party files local only:

- `integrations/whisper/bin/`: local `whisper.cpp` executable, for example `whisper-cli.exe`.
- `integrations/whisper/models/`: local Whisper model files, for example `ggml-base.bin`.
- `integrations/whisper/config.json`: local configuration copied from `config.example.json`.

Expected workflow:

1. Build or place `whisper.cpp` locally in `integrations/whisper/bin/`.
2. Place a Whisper model locally in `integrations/whisper/models/`.
3. Copy `config.example.json` to `config.json`.
4. Adjust paths if needed.
5. Restart Kivrio Chat so the backend can use the local configuration.

The Micro button records a short WAV clip in the browser, sends it to the local backend, and inserts the transcription into the composer. It does not send the prompt automatically.
