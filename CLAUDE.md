# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A push-to-talk .NET 10 console app: microphone → whisper.cpp (speech-to-text) → Claude tool-use loop → Piper
(text-to-speech) → speaker. It is the companion code for a two-part dev.to article series (linked in README.md),
so code comments explain *why* and often reference "Part 1"/"Part 2". Keep that style when editing.

## Commands

```bash
dotnet build                                   # CI runs: restore, build -c Release, test -c Release --no-build
dotnet test                                    # all tests (no Docker, audio or API key needed)
dotnet test --filter "FullyQualifiedName~SummarizeBuildTests"   # one class; use ~<TestName> for one test
docker compose up -d                           # whisper on 127.0.0.1:8080, piper on 127.0.0.1:5000
dotnet run --project src/VoiceDevAgent -- <repo path>          # voice mode
dotnet run --project src/VoiceDevAgent -- <repo path> --text   # type requests, replies still spoken
WHISPER_MODEL=base.en docker compose up -d     # bigger model (download models/ggml-base.en.bin first)
```

- Requires `ANTHROPIC_API_KEY` (checked at startup) and a Whisper model in `models/` (e.g. `ggml-tiny.en.bin`,
  download URL in README). Startup pings both containers and exits with a hint if they are down.
- Tests (`tests/VoiceDevAgent.Tests`, xUnit) cover only pure logic: `IsBlank`, `GuardSpoken`, `Wav`, and the
  `DevTools.Summarize*` parsers. Tools are split into "run the process" and a pure `Summarize*` function so the
  parsing can be tested without git or dotnet; keep that split when adding a tool. The test project sees
  `internal` members via `InternalsVisibleTo`; test classes are in the global namespace like the app.
- CI (`.github/workflows/ci.yml`) also validates that `docker compose config` resolves the whisper `command` to a
  single string; keep that one-element list form (the image's entrypoint is `bash -c`, so a plain string silently
  drops every flag).
- SDK pinned by `global.json` (10.0.100, `rollForward: latestFeature`).

## Architecture

`ARCHITECTURE.md` has the full picture (turn lifecycle, error-handling table, infrastructure). Record notable
changes in `CHANGELOG.md` under `[Unreleased]`.

All code is in `src/VoiceDevAgent/` (single project, no namespaces, top-level statements in `Program.cs`).

- **`Program.cs`** — the session loop: record (or read a line), transcribe, `VoiceAgent.AskAsync`, synthesize,
  play. Handles barge-in (Enter during playback cancels it; that same keypress starts the next recording via
  `alreadyPressed`). Every stage catches its own transport errors and continues the loop rather than exiting;
  audio failures arrive as `AudioDeviceException` (PortAudio errors are wrapped in `Audio.cs`). Speakers are
  optional (`AudioDevices`): without them replies are only printed.
  `Vocabulary` is Whisper's initial prompt (spelling hints).
- **`VoiceAgent.cs`** — manual tool-use loop on `claude-haiku-5-5` via the official `Anthropic` NuGet SDK, max 8
  steps per turn, low effort, cached system prompt. Invariants that matter:
  - Every response block (including thinking/redacted thinking) is echoed back unchanged in the history.
  - All `tool_result`s for a step go in one user message.
  - On any exception, or when the step limit runs out, the whole turn is removed from `messages` so no
    `tool_use` is left without a `tool_result`.
  - Two output channels: final text is spoken; `show_on_screen` tool output goes to the console.
    `GuardSpoken` enforces `MaxSpokenWords` (30) in code — longer replies go to the screen and only the first
    sentence is spoken.
  - `IsBlank` filters Whisper's `[BLANK_AUDIO]`/`(silence)` markers before any API call.
  - Tool failures are returned to the model as `is_error` results, not thrown. The `SystemPrompt` describes a
    specific repo (ClaudeReviewBot) and is meant to be customized.
- **`DevTools.cs`** — the read-only tools (`git_status`, `dotnet_build`, `dotnet_test`) plus `RunAsync`. Tools
  condense raw output for the model: porcelain v2 parsing, deduplicated MSBuild diagnostics (errors first, capped
  at 50), TRX parsing for test failures with only own-code frames. The exit code is the verdict even if nothing
  parsed. `RunAsync` uses `ArgumentList` (never a concatenated command string; model-supplied
  values are validated first, e.g. `ResolveProject`), strips inherited `MSBuild*` env
  vars so the target repo's `global.json` SDK is used, and enforces a 5-minute timeout.
- **`Speech.cs`** — thin HTTP clients: `WhisperClient` (`POST /inference`, multipart) and `PiperClient`
  (`POST /synthesize`, returns WAV).
- **`Audio.cs`** — PortAudio capture/playback via `PortAudioSharp2`. Capture is 16 kHz mono 16-bit (Whisper's
  format, no resampling). Audio callbacks must not block or allocate unnecessarily: capture writes to an unbounded
  channel; playback pads with a preallocated silence buffer. `Wav.Decode` walks RIFF chunks rather than assuming a
  44-byte header.
- **`piper/Dockerfile`** — Piper HTTP server with the `en_US-lessac-medium` voice baked in.

## Design constraints

- Project skills live in `.claude/skills`. Use `add-tool` for any change to the agent's tools; it holds the
  argument-validation rules.

- Tools stay read-only on purpose (a misheard request should at worst run a build). Any tool with side effects
  must confirm in code, not via the prompt.
- English only as configured (`.en` Whisper models, English Piper voice).
- `.editorconfig` sets 2-space indentation for all files, but the C# sources use 4 spaces — match the existing
  file you are editing.
