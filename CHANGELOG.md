# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). Each release has a matching `v<version>` git tag.

## [Unreleased]

## [1.1.0] - 2026-10-22

### Added

- `CLAUDE.md` with build and run commands and an architecture summary for Claude Code.
- `ARCHITECTURE.md` describing the voice loop, the tool-use loop and the reasoning behind its design.
- `CHANGELOG.md`.
- An xUnit test project (`tests/VoiceDevAgent.Tests`) for `IsBlank`, `GuardSpoken`, WAV encoding and decoding,
  and the `git status`, build and TRX summaries. CI runs it.
- Claude Code skills in `.claude/skills`: `run-agent` and `bump-images` (manual only), `add-tool` and
  `update-changelog`. Each one has explicit security rules.
- `models/README.md` explains why the folder ships empty. It lists each Whisper model with its size, measured
  speed and error rate, and gives the download command for bash and for Windows PowerShell, where `curl` is an
  alias for `Invoke-WebRequest`.
- `.env.template` lists the environment variables the agent reads.

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

## [1.0.0] - 2026-10-22

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

[Unreleased]: https://github.com/Juxn89/dotnet-voice-dev-agent/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/Juxn89/dotnet-voice-dev-agent/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/Juxn89/dotnet-voice-dev-agent/releases/tag/v1.0.0
