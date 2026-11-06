using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Models.Messages;

/// <summary>
/// Part 1's tool-use loop with two output channels: the final text is spoken, and anything
/// that can't be listened to goes to the screen through the show_on_screen tool. Part 3 adds
/// a memory that outlives the session, so nothing about the repository is hardcoded here.
/// </summary>
sealed class VoiceAgent
{
    const string Model = "claude-haiku-5-5";

    // About ten seconds of listening at Piper's pace. Longer replies go to the screen.
    const int MaxSpokenWords = 30;

    internal const string BasePrompt = """
        You are a development assistant for a .NET repository, and the developer is TALKING
        to you. Their words arrive through speech-to-text, and your reply is read aloud by a
        text-to-speech voice.

        The transcript is often wrong. Speech-to-text mangles identifiers and jargon: a class
        name may arrive as two ordinary words, "build red" as "view right". Read for intent.
        If you can tell what was meant, act on it. If two readings would lead to different
        actions, ask one short question instead of guessing.

        Your reply is heard, not read. So:
        - Speak at most two short sentences. No lists, no markdown, no code.
        - Never say a file path, stack frame, hash, or full identifier aloud. Put them on
          the screen with show_on_screen, then say what matters and "details on screen".
        - Lead with the answer: "Build's red, three errors, all from your rename."
        - Always end with something to say, even when the details went to the screen.

        Check state before answering. If the question is about the build, build. If it is
        about what changed, call git_status. To see code, use read_file. Do not speculate.
        """;

    const string MemoryRules = """

        You have a memory directory that survives between sessions, one per repository.
        - Save what the tools cannot tell you: which project matters and why, conventions,
          what the developer is in the middle of and plans to do next, decisions and why.
        - Never save what a tool can tell you: whether the build passes, errors and their
          line numbers, test results, the list of changed files. All of it is out of date by
          the next session; run the tool instead.
        - Write file and project paths in full, relative to the repository root.
        - Keep one file, /memories/project.md, under 40 lines. Date what you write about work
          in progress. Update or remove entries that stop being true; do not append duplicates.
        - Save quietly: do not mention memory in your spoken reply unless asked.
        """;

    const string VerifyRule = """

        A memory is a note from an earlier session, not a fact about now. Before you act on
        one or repeat it as current, check it with a tool. When a tool contradicts a memory,
        trust the tool, fix the memory, and say in a few words what changed.
        """;

    internal static readonly List<ToolUnion> DevToolDefinitions =
    [
        new Tool
        {
            Name = "git_status",
            Description = "Branch, ahead/behind counts, and changed and untracked files in the working copy.",
            InputSchema = new() { Properties = new Dictionary<string, JsonElement>() },
        },
        new Tool
        {
            Name = "dotnet_build",
            Description = "Builds the solution, or one project, and returns deduplicated errors and warnings.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["project"] = JsonSerializer.SerializeToElement(new
                    {
                        type = "string",
                        description = "Optional path to a .csproj or .sln, relative to the repository root.",
                    }),
                },
            },
        },
        new Tool
        {
            Name = "dotnet_test",
            Description = "Runs the test suite and returns pass/fail counts plus each failure's message and own-code frames.",
            InputSchema = new() { Properties = new Dictionary<string, JsonElement>() },
        },
        new Tool
        {
            Name = "read_file",
            Description = "Reads up to 200 numbered lines of a file in the repository. Read-only.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["path"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Path relative to the repository root." }),
                    ["start_line"] = JsonSerializer.SerializeToElement(new { type = "integer" }),
                    ["end_line"] = JsonSerializer.SerializeToElement(new { type = "integer" }),
                },
                Required = ["path"],
            },
        },
        new Tool
        {
            Name = "show_on_screen",
            Description = "Shows text on the developer's screen without reading it aloud. Use for paths, " +
                          "diagnostics, stack frames, lists: anything too long or too precise to listen to.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["text"] = JsonSerializer.SerializeToElement(new { type = "string" }),
                },
                Required = ["text"],
            },
        },
    ];

    readonly AnthropicClient client;
    readonly string repo;
    readonly MemoryStore? memory;
    readonly AgentSetup setup;
    readonly List<ToolUnion> tools;
    readonly List<TextBlockParam> system;
    readonly string sessionNote;

    internal readonly List<MessageParam> Messages = [];

    public VoiceAgent(AnthropicClient client, string repo, MemoryStore? memory, DateOnly today)
        : this(client, repo, memory, today, AgentSetup.Default)
    {
    }

    internal VoiceAgent(AnthropicClient client, string repo, MemoryStore? memory, DateOnly today, AgentSetup setup)
    {
        this.client = client;
        this.repo = repo;
        this.memory = memory;
        this.setup = setup;
        tools = memory is null ? DevToolDefinitions : [.. DevToolDefinitions, new MemoryTool20250818()];

        // The system prompt is built once and frozen for the whole session. Writes to memory
        // during the session go to disk and show up in the NEXT session's prompt. Changing
        // the system prompt mid-session would break the prompt cache, and on Haiku 5.5 it
        // also invalidates every thinking block already in the history.
        string prompt = BasePrompt;
        if (memory is not null) prompt += MemoryRules;
        if (memory is not null && setup.VerifyRule) prompt += VerifyRule;

        string context = "";
        if (memory is not null && setup.PreloadMemory)
        {
            string snapshot = memory.Snapshot();
            context = snapshot.Length == 0
                ? "Your memory directory is empty. This is the first session in this repository."
                : "Your memory, loaded when this session started. You do not need to view it again:\n" + snapshot;
        }
        if (setup.Notes is not null) context = "Notes from your previous session:\n" + setup.Notes;

        system = context.Length == 0
            ? [new() { Text = prompt, CacheControl = new CacheControlEphemeral() }]
            : [new() { Text = prompt }, new() { Text = context, CacheControl = new CacheControlEphemeral() }];

        // The date goes in the session's first message, not in the system prompt, so a
        // history carried from an earlier day is still a prefix of the next request.
        sessionNote = $"[Session started {today.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture)}.]\n";

        if (setup.History is not null) Messages.AddRange(setup.History);
    }

    /// <summary>
    /// Whisper marks silence and noise with bracketed tags: [BLANK_AUDIO], [inaudible],
    /// (silence), [Music]. A transcript that is nothing but tags is not a request: an
    /// accidental push-to-talk press must not cost an API call or run a tool.
    /// </summary>
    public static bool IsBlank(string transcript) =>
        Regex.Replace(transcript, @"\[[^\]]*\]|\([^)]*\)", "").Trim().Trim('.', ' ', '>').Length == 0;

    /// <summary>One spoken request in, one spoken reply out. Screen output goes to <paramref name="screen"/>.</summary>
    public async Task<string> AskAsync(string transcript, Action<string> screen, CancellationToken ct)
    {
        if (IsBlank(transcript)) return "";

        int turnStart = Messages.Count;
        bool firstOfSession = !Messages.Skip(setup.History?.Count ?? 0).Any();
        Messages.Add(new() { Role = Role.User, Content = (firstOfSession ? sessionNote : "") + transcript });

        try
        {
            bool shown = false;
            string? reply = await RunTurnAsync(text => { shown = true; screen(text); }, ct);
            // A turn can end on a tool call with no text at all. Spoken, that is silence, and
            // silence sounds like the agent hung. Never return nothing.
            if (reply?.Length == 0) reply = shown ? "Details are on screen." : "Okay.";
            if (reply is not null) return reply;
        }
        catch
        {
            // A failed request can leave a tool_use with no tool_result at the end of the
            // history, and every later request would be rejected. Drop the whole turn.
            Messages.RemoveRange(turnStart, Messages.Count - turnStart);
            throw;
        }

        Messages.RemoveRange(turnStart, Messages.Count - turnStart);
        return "I went in circles on that one. Try asking differently.";
    }

    /// <summary>Returns the spoken reply, or null if the step limit ran out.</summary>
    async Task<string?> RunTurnAsync(Action<string> screen, CancellationToken ct)
    {
        for (int step = 0; step < 8; step++)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            Message response = await client.Messages.Create(new MessageCreateParams
            {
                Model = Model,
                MaxTokens = 4096,
                System = system,
                Tools = tools,
                OutputConfig = new OutputConfig { Effort = Effort.Low },
                // Caches the conversation up to its last block, so each step of a turn
                // reads the history from cache instead of paying for it again.
                CacheControl = new CacheControlEphemeral(),
                Messages = Messages,
            }, ct);
            setup.OnResponse?.Invoke(response, elapsed.Elapsed);

            if (response.StopReason == "refusal")
            {
                // Haiku 5.5 has no server-side fallback, so the request stays declined.
                throw new RefusalException();
            }

            // Echo every block back unchanged - thinking blocks included, even when their
            // text is empty - or the next request fails the history check.
            List<ContentBlockParam> echo = [];
            List<ContentBlockParam> results = [];
            string spoken = "";

            foreach (ContentBlock block in response.Content)
            {
                if (block.TryPickThinking(out ThinkingBlock? thinking))
                {
                    echo.Add(new ThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                }
                else if (block.TryPickRedactedThinking(out RedactedThinkingBlock? redacted))
                {
                    echo.Add(new RedactedThinkingBlockParam { Data = redacted.Data });
                }
                else if (block.TryPickText(out TextBlock? text))
                {
                    echo.Add(new TextBlockParam { Text = text.Text });
                    spoken += text.Text;
                }
                else if (block.TryPickToolUse(out ToolUseBlock? use))
                {
                    echo.Add(new ToolUseBlockParam { ID = use.ID, Name = use.Name, Input = use.Input });
                    (string content, bool isError) = await RunToolAsync(use, screen, ct);
                    results.Add(new ToolResultBlockParam { ToolUseID = use.ID, Content = content, IsError = isError });
                }
            }

            Messages.Add(new() { Role = Role.Assistant, Content = echo });

            if (results.Count == 0) return GuardSpoken(spoken.Trim(), screen);

            // All results for one turn go back in ONE user message.
            Messages.Add(new() { Role = Role.User, Content = results });
        }

        return null;
    }

    async Task<(string Content, bool IsError)> RunToolAsync(ToolUseBlock use, Action<string> screen, CancellationToken ct)
    {
        try
        {
            switch (use.Name)
            {
                case "git_status":
                    return (await DevTools.GitStatusAsync(repo, ct), false);
                case "dotnet_build":
                    string? project = null;
                    if (use.Input.TryGetValue("project", out JsonElement p) && p.ValueKind == JsonValueKind.String
                        && p.GetString() is string requested && !string.IsNullOrWhiteSpace(requested))
                    {
                        (project, string? refused) = DevTools.ResolveProject(repo, requested);
                        if (refused is not null) return (refused, true);
                    }
                    return (await DevTools.DotnetBuildAsync(repo, project, ct), false);
                case "dotnet_test":
                    return (await DevTools.DotnetTestAsync(repo, ct), false);
                case "read_file":
                    return DevTools.ReadFile(repo, use.Input["path"].GetString() ?? "",
                        OptionalInt(use, "start_line"), OptionalInt(use, "end_line"));
                case "show_on_screen":
                    screen(use.Input["text"].GetString() ?? "");
                    return ("Shown.", false);
                case "memory" when memory is not null:
                    return memory.Run(use.Input);
                default:
                    return ($"Unknown tool {use.Name}.", true);
            }
        }
        // A tool that can't run is an answer for the model ("I couldn't build"), not a crash.
        // Win32Exception is what Process.Start throws for a missing executable or a bad
        // working directory; UnauthorizedAccessException is a file the user can't read.
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or KeyNotFoundException
                                       or Win32Exception or IOException or UnauthorizedAccessException)
        {
            return (ex.Message, true);
        }
    }

    static int? OptionalInt(ToolUseBlock use, string name) =>
        use.Input.TryGetValue(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int n) ? n : null;

    /// <summary>
    /// The prompt asks for short replies; this makes sure of it. A reply too long to listen
    /// to goes to the screen, and only its first sentence is spoken.
    /// </summary>
    internal static string GuardSpoken(string spoken, Action<string> screen)
    {
        string[] words = spoken.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= MaxSpokenWords) return spoken;

        screen(spoken);
        // A sentence ends at . ? ! followed by a space or the end - not inside
        // "ChangedFile.cs" or "1.8 seconds".
        Match end = Regex.Match(spoken, @"[.?!](\s|$)");
        string first = end.Success ? spoken[..(end.Index + 1)] : spoken;
        string[] firstWords = first.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (firstWords.Length > MaxSpokenWords) first = string.Join(' ', firstWords.Take(MaxSpokenWords)) + "...";
        return $"{first} The rest is on screen.";
    }
}

/// <summary>
/// What the measurements in Part 3 vary. The app always runs <see cref="Default"/>: memory
/// preloaded into the prompt, and the rule that a memory is checked before it is trusted.
/// </summary>
internal sealed record AgentSetup(
    bool PreloadMemory = true,
    bool VerifyRule = true,
    string? Notes = null,
    IReadOnlyList<MessageParam>? History = null,
    Action<Message, TimeSpan>? OnResponse = null)
{
    public static readonly AgentSetup Default = new();
}

sealed class RefusalException() : Exception("The model declined this request.");
