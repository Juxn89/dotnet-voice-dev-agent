---
name: add-tool
description: Add or change a tool the voice agent can call (git, dotnet or any other process). Covers the tool definition, argument validation, the pure Summarize* parser, tests and docs. Use whenever VoiceAgent.Tools, RunToolAsync or DevTools changes.
---

# Add a tool to the voice agent

Tool arguments come from the model. The model's context includes a speech-to-text transcript that is often
wrong, plus build, test and git output from a repository that may contain text written to manipulate the
agent. Treat every tool argument as untrusted input.

## Security rules (not optional)

1. **Read-only by default.** A misheard request should at worst run a build. If a tool changes anything
   (files, git state, packages, network), the confirmation goes **in code**: print exactly what will run, read
   `y` from the console in `Program.cs`, and refuse otherwise. A sentence in the system prompt is not a
   confirmation.
2. **No shell.** Start processes only through `DevTools.RunAsync`, which uses `ProcessStartInfo.ArgumentList`.
   Never `bash -c`, `cmd /c`, `pwsh -c`, and never a command string built from model input.
3. **Validate every model-supplied argument** before it reaches `RunAsync`:
   - Reject values that start with `-`. `ArgumentList` stops shell injection, not option injection
     (`-p:...`, `-bl:<path>` and `--source` are all accepted by `dotnet`).
   - For a path: `Path.GetFullPath(Path.Combine(repo, value))` must stay under `repo`, the file must exist, and
     the extension must be on an allowlist (for example `.csproj`, `.sln`, `.slnx`).
   - For anything else: an allowlist or a strict regex, never a denylist.
   - On a rejected value, return an `is_error` tool result that explains why. Don't throw.
4. **Bounded output.** Condense and cap what goes back to the model, as the existing tools do (50 diagnostics,
   40 changed files, 15 failures). Raw output wastes context and carries more injected text.
5. **Keep the timeout.** `RunAsync`'s five-minute limit and process-tree kill apply to every tool.

`DevTools.ResolveProject` (used by `dotnet_build`) is the reference implementation of rule 3, and
`ResolveProjectTests` shows the cases to cover.

## Steps

1. **Definition.** Add a `Tool` to `VoiceAgent.Tools`. In its description, say what it returns, not how.
2. **Dispatch.** Add a `case` in `VoiceAgent.RunToolAsync`. If the tool can throw a new exception type for an
   expected failure, add it to the `catch` filter there. Don't use `catch (Exception)`.
3. **Implementation.** In `DevTools`, write an `async` method that validates its arguments, calls `RunAsync`
   and checks the exit code. Then add an `internal static string Summarize<Thing>(...)` that turns raw output
   into the model's text. The exit code is the verdict: a non-zero exit with nothing parsed is still a failure.
4. **Tests.** In `tests/VoiceDevAgent.Tests/DevToolsTests.cs`, test `Summarize<Thing>` with literal output.
   Test the argument validation too, including `-` prefixes, `..` escapes and absolute paths.
5. **Prompt.** Change `VoiceAgent.SystemPrompt` only if the model needs to know when to use the tool.
6. **Docs.** Update the tools table in `ARCHITECTURE.md`, the tool list in `README.md` and `[Unreleased]` in
   `CHANGELOG.md`.
7. **Verify.** Run `dotnet build -c Release` and `dotnet test`.
