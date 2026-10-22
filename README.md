# dotnet-voice-dev-agent

A .NET 10 dev agent you talk to. Hold Enter, ask *"why is the build red?"*, and it runs the build, puts the
diagnostics on screen, and answers out loud in one or two sentences.

Speech-to-text and text-to-speech run locally ([whisper.cpp](https://github.com/ggml-org/whisper.cpp) and
[Piper](https://github.com/OHF-Voice/piper1-gpl), in Docker). The agent is Claude Haiku 5.5 with three read-only
tools: `git_status`, `dotnet_build`, `dotnet_test`, plus `show_on_screen` for anything too long or too precise
to listen to.

This is the companion code for a two-part series. The articles explain every design decision and the
measurements behind it:

- **Part 1: Teach It Your Actual Workflow**:
  [English](https://dev.to/jgomezdev/building-a-net-dev-agent-part-1-teach-it-your-actual-workflow-5826) ·
  [Español](https://dev.to/jgomezdev/construye-un-agente-de-desarrollo-net-parte-1-ensenale-tu-flujo-de-trabajo-real-3580).
  The tools, and why they filter what the build and tests print.
- **Part 2: Give It a Voice**:
  [English](https://dev.to/jgomezdev/building-a-net-dev-agent-part-2-give-it-a-voice-1ef3) ·
  [Español](https://dev.to/jgomezdev/construye-un-agente-de-desarrollo-net-parte-2-dale-voz-53an).
  The voice loop, and what changes when an answer has to be heard instead of read.

## Requirements

- .NET 10 SDK
- Docker
- A microphone and headphones (laptop speakers feed the answer back into the microphone)
- An Anthropic API key in `ANTHROPIC_API_KEY`

Audio goes through [PortAudioSharp2](https://www.nuget.org/packages/PortAudioSharp2), which ships PortAudio's
native binaries for Windows, Linux and macOS inside the package. There is nothing else to install.

## Run it

```bash
# 1. A Whisper model (tiny.en is the only one fast enough to talk to on a laptop CPU, see below)
curl -L -o models/ggml-tiny.en.bin https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.en.bin

# 2. Speech-to-text on :8080, text-to-speech on :5000
docker compose up -d

# 3. Point it at a .NET repository
export ANTHROPIC_API_KEY=...        # PowerShell: $env:ANTHROPIC_API_KEY = "..."
dotnet run --project src/VoiceDevAgent -- /path/to/your/repo
```

Enter starts listening, Enter stops. While it speaks, Enter cuts it off; after it finishes, Enter starts the
next request. `--text` swaps the microphone for the keyboard and keeps the voice:

```bash
dotnet run --project src/VoiceDevAgent -- /path/to/your/repo --text
```

To use a bigger model: `WHISPER_MODEL=base.en docker compose up -d` (after downloading `ggml-base.en.bin`).

## Make it yours

The system prompt in [`VoiceAgent.cs`](src/VoiceDevAgent/VoiceAgent.cs) describes the repository the articles
used (ClaudeReviewBot). Replace that paragraph with your own projects and rules: which project matters, which
warnings are noise. That is where the agent learns your workflow.

The `Vocabulary` constant in [`Program.cs`](src/VoiceDevAgent/Program.cs) is Whisper's initial prompt. Put your
project's type names, error codes and jargon there. It fixes exactly the words speech-to-text breaks most.

## What to expect

Measured on a 4-core laptop CPU, one speaker (the author), eight sentences. A measured anecdote, not a
benchmark. The articles have the full tables.

| Whisper model | Word error rate (with vocabulary prompt) | Median per sentence |
|---|---|---|
| `tiny.en` | 0.68 | 1.8 s |
| `base.en` | 0.59 | 5.6 s |
| `small.en` | 0.48 | 13.4 s |
| `large-v3-turbo` | 0.41 | over a minute |

- **The transcript is often wrong, and the agent is built for that.** "Why is the build red?" arrived as "What
  is the view right?" and the agent still ran the build. When a transcript can mean two different actions, it
  asks. Every turn prints `heard: "..."`, so you see a misheard word before you trust the answer.
- **About 7 to 12 seconds from releasing Enter to the first spoken word**: speech-to-text, the model and its
  tools, and synthesis.
- **English only, as configured.** The `.en` models do not reject other languages; they return fluent English
  you never said. Speaking Spanish needs a multilingual Whisper model and a Spanish Piper voice.

## Design notes

- **Two output channels.** The model's final text is spoken; `show_on_screen` takes paths, stack frames and
  lists. A reply over 30 words is moved to the screen in code, because the prompt asks for brevity but cannot
  guarantee it.
- **Read-only tools on purpose.** A misheard request can at worst run a build. Before adding a tool with side
  effects, make it ask for confirmation in code, not in the prompt.
- **Failures stay inside the conversation.** A tool that cannot run returns an error to the model. A failed API
  request drops the whole turn from the history, so a `tool_use` is never left without its `tool_result`.
- **Silence never reaches the model.** Whisper's `[BLANK_AUDIO]`-style markers are filtered before any API
  call.
- **The exit code is the verdict.** `RunAsync` returns it with the output, and a non-zero exit with no parsed
  error still reads as a failure.

## License

[MIT](LICENSE)
