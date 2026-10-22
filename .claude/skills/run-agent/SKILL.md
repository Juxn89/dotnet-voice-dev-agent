---
name: run-agent
description: Start whisper and piper, then run the voice dev agent in --text mode against a .NET repository as a smoke test. Use to see a change working in the real app, not just in tests.
disable-model-invocation: true
---

# Run the voice dev agent

Runs the real app end to end. It costs API credits, and the agent's tools build and test the target
repository, which **executes that repository's code** (MSBuild targets, test code). Follow every rule below.

## Rules

- **API key:** check only that it is set: `[ -n "$ANTHROPIC_API_KEY" ] && echo set || echo missing`. Never
  print, echo, log or `cat` it, and never read `.env`. If it is missing, ask the user to set it in their own
  terminal. Do not ask them to paste it into the chat.
- **Target repository:** default to this repository. Run against another path only if the user names it, and
  confirm that they trust it, because the build and test tools run its code.
- **Downloads:** the only download is the Whisper model, from the exact URL below. Don't fetch models or
  scripts from anywhere else.
- Don't change `docker-compose.yml`, `piper/Dockerfile` or the image tags to make something work. Report the
  problem instead (`/bump-images` is the reviewed path for image changes).

## Steps

1. **Model.** If `models/ggml-tiny.en.bin` is missing:

   ```bash
   curl -fL -o models/ggml-tiny.en.bin https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.en.bin
   ```

   The file should be about 75 MB. If it is a few KB, it is an error page: delete it and stop.

2. **Containers.** Run `docker compose up -d`, then wait until both services answer:

   ```bash
   curl -sf -o /dev/null -X POST -H 'Content-Type: application/json' -d '{"text":"ok"}' http://localhost:5000/synthesize && echo piper up
   curl -s -o /dev/null -w '%{http_code}\n' http://localhost:8080/
   ```

   The first Piper build downloads its voice and takes a few minutes. Remember whether the containers were
   already running before this step.

3. **Run in text mode.** It reads one request per line and quits on an empty line, so it can be driven
   without a microphone:

   ```bash
   printf 'what changed?\nis the build green?\n\n' | dotnet run --project src/VoiceDevAgent -- <repo path> --text
   ```

   The replies are also spoken through the default output device.

4. **Check the output.**
   - Each request prints `says: "..."` with at most two short sentences.
   - Anything longer appears in a `┌ screen` box.
   - There is no unhandled exception.
   - `request failed` means the API could not be reached, not a code bug. Report it as an API failure.

5. **Clean up.** If you started the containers in step 2, run `docker compose down`.

Voice mode (no `--text`) needs a person at the microphone. Tell the user the command instead of running it.
