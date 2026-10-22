# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). The project has no version tags
yet, so releases are listed by date.

## [Unreleased]

### Added

- `CLAUDE.md` with build and run commands and an architecture summary for Claude Code.
- `ARCHITECTURE.md` describing the voice loop, the tool-use loop and the reasoning behind its design.
- `CHANGELOG.md`.
- An xUnit test project (`tests/VoiceDevAgent.Tests`) for `IsBlank`, `GuardSpoken`, WAV encoding and decoding,
  and the `git status`, build and TRX summaries. CI runs it.
- Claude Code skills in `.claude/skills`: `run-agent` and `bump-images` (manual only), `add-tool` and
  `update-changelog`. Each one has explicit security rules.

### Changed

- `DevTools` parsing moved into pure `SummarizeStatus`, `SummarizeBuild` and `SummarizeTrx` functions so it can
  be tested without running processes.

### Security

- `dotnet_build` validates its `project` argument before running anything. A value that starts with `-` (option
  injection such as `-bl:<path>` or `-p:...`), resolves outside the repository, is not a project or solution,
  or does not exist is refused, and the reason goes back to the model as an error.

### Fixed

- Without speakers or headphones, the Piper container is no longer required at startup. Its check now runs after
  the devices are detected, and only when there is an output device.
- Build diagnostics inside the repository are shown relative to it even when the repository path was given as
  `.`, as another relative path, or with a trailing separator. A sibling directory whose name starts with the
  repository's is no longer treated as part of it.

- `git_status` reported the behind count with a minus sign (`-1 behind`), because git writes it as `-<n>`.
- A missing, busy or unplugged microphone or output device no longer ends the session with a stack trace.
  Device failures are reported as `AudioDeviceException` and the session continues. At startup, a missing
  microphone in voice mode exits with a hint to use `--text`. Without speakers, replies are printed only.

## 2026-10-22

### Changed

- CI now uses the Node 24 releases of `actions/checkout` (v7) and `actions/setup-dotnet` (v6).

### Added

- First release, the companion code for *Building a .NET Dev Agent, Part 2: Give It a Voice*.
- Push-to-talk microphone capture and playback through PortAudioSharp2, with barge-in (Enter cuts the reply
  off).
- Local speech-to-text (whisper.cpp `whisper-server`) and text-to-speech (Piper, `en_US-lessac-medium`), both
  run with `docker compose`.
- A Claude Haiku 5.5 tool-use loop with Part 1's read-only tools (`git_status`, `dotnet_build`, `dotnet_test`)
  plus `show_on_screen`.
- A `--text` mode that takes typed requests and still speaks the reply.
- CI that builds the solution and checks that the whisper `command` in the compose file resolves to a single
  command line.

### Fixed

- Child `dotnet build`/`dotnet test` processes no longer inherit the `MSBuild*` variables that `dotnet run`
  exports, so the target repository's `global.json` chooses the SDK (Part 1 fix).
- The exit code decides whether a build succeeded. A non-zero exit with no parsed diagnostics is reported as a
  failure (Part 1 fix).
