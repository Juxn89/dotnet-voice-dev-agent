using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Models.Messages;

/// <summary>
/// Part 1's tool-use loop with two output channels: the final text is spoken, and anything
/// that can't be listened to goes to the screen through the show_on_screen tool.
/// </summary>
sealed class VoiceAgent(AnthropicClient client, string repo)
{
    const string Model = "claude-haiku-5-5";

    // About ten seconds of listening at Piper's pace. Longer replies go to the screen.
    const int MaxSpokenWords = 30;

    const string SystemPrompt = """
        You are a development assistant for the ClaudeReviewBot repository, and the
        developer is TALKING to you. Their words arrive through speech-to-text, and your
        reply is read aloud by a text-to-speech voice.

        Three projects. src/ClaudeReviewBot.Core holds the logic and is the one that
        matters. src/ClaudeReviewBot is a thin CLI wrapper. tests/ is xUnit.
        TreatWarningsAsErrors is on. CS1591 is noise; nullable warnings are real defects.

        The transcript is often wrong. Speech-to-text mangles identifiers and jargon:
        "ChangedFile" may arrive as "changed file" or "unchanged file", "build red" as
        "view right". Read for intent. If you can tell what was meant, act on it. If two
        readings would lead to different actions, ask one short question instead of guessing.

        Your reply is heard, not read. So:
        - Speak at most two short sentences. No lists, no markdown, no code.
        - Never say a file path, stack frame, hash, or full identifier aloud. Put them on
          the screen with show_on_screen, then say what matters and "details on screen".
        - Lead with the answer: "Build's red, three errors, all from your rename."

        Check state before answering. If the question is about the build, build. If it is
        about what changed, call git_status. Do not speculate.
        """;

    static readonly List<ToolUnion> Tools =
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
                        description = "Optional project path relative to the repository root.",
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

    readonly List<MessageParam> messages = [];

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

        int turnStart = messages.Count;
        messages.Add(new() { Role = Role.User, Content = transcript });

        try
        {
            string? reply = await RunTurnAsync(screen, ct);
            if (reply is not null) return reply;
        }
        catch
        {
            // A failed request can leave a tool_use with no tool_result at the end of the
            // history, and every later request would be rejected. Drop the whole turn.
            messages.RemoveRange(turnStart, messages.Count - turnStart);
            throw;
        }

        messages.RemoveRange(turnStart, messages.Count - turnStart);
        return "I went in circles on that one. Try asking differently.";
    }

    /// <summary>Returns the spoken reply, or null if the step limit ran out.</summary>
    async Task<string?> RunTurnAsync(Action<string> screen, CancellationToken ct)
    {
        for (int step = 0; step < 8; step++)
        {
            Message response = await client.Messages.Create(new MessageCreateParams
            {
                Model = Model,
                MaxTokens = 4096,
                System = new List<TextBlockParam> { new() { Text = SystemPrompt, CacheControl = new CacheControlEphemeral() } },
                Tools = Tools,
                OutputConfig = new OutputConfig { Effort = Effort.Low },
                Messages = messages,
            }, ct);

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

            messages.Add(new() { Role = Role.Assistant, Content = echo });

            if (results.Count == 0) return GuardSpoken(spoken.Trim(), screen);

            // All results for one turn go back in ONE user message.
            messages.Add(new() { Role = Role.User, Content = results });
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
                    string? project = use.Input.TryGetValue("project", out JsonElement p) && p.ValueKind == JsonValueKind.String
                        ? p.GetString()
                        : null;
                    return (await DevTools.DotnetBuildAsync(repo, project, ct), false);
                case "dotnet_test":
                    return (await DevTools.DotnetTestAsync(repo, ct), false);
                case "show_on_screen":
                    screen(use.Input["text"].GetString() ?? "");
                    return ("Shown.", false);
                default:
                    return ($"Unknown tool {use.Name}.", true);
            }
        }
        // A tool that can't run is an answer for the model ("I couldn't build"), not a crash.
        // Win32Exception is what Process.Start throws for a missing executable or a bad
        // working directory.
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or KeyNotFoundException
                                       or Win32Exception or IOException)
        {
            return (ex.Message, true);
        }
    }

    /// <summary>
    /// The prompt asks for short replies; this makes sure of it. A reply too long to listen
    /// to goes to the screen, and only its first sentence is spoken.
    /// </summary>
    static string GuardSpoken(string spoken, Action<string> screen)
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

sealed class RefusalException() : Exception("The model declined this request.");
