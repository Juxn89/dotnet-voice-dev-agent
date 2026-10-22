using System.Diagnostics;
using Anthropic;
using Anthropic.Exceptions;
using PortAudioSharp;

// usage: dotnet run -- <repo path> [--text]
//   --text  type instead of talking (the reply is still spoken)
if (args.Length == 0)
{
    Console.Error.WriteLine("usage: dotnet run -- <repo path> [--text]");
    return 2;
}

string repo = args[0];
bool typed = args.Contains("--text");

// Whisper's initial prompt: spelling hints for the words it gets wrong most.
const string Vocabulary =
    "dotnet build, dotnet test, git status, xUnit, ClaudeReviewBot, ChangedFile, CS1061, nullable, origin main.";

// Fail before opening the microphone, not after the first recording.
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
{
    Console.Error.WriteLine("ANTHROPIC_API_KEY is not set.");
    return 1;
}

using HttpClient whisperHttp = new() { BaseAddress = new Uri("http://localhost:8080"), Timeout = TimeSpan.FromMinutes(2) };
using HttpClient piperHttp = new() { BaseAddress = new Uri("http://localhost:5000"), Timeout = TimeSpan.FromMinutes(2) };
WhisperClient whisper = new(whisperHttp);
PiperClient piper = new(piperHttp);

try
{
    await piper.SynthesizeAsync("ready", CancellationToken.None);
    if (!typed)
        await whisper.TranscribeAsync(Wav.Encode(new short[Microphone.SampleRate / 2], Microphone.SampleRate), null, "en", CancellationToken.None);
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"Start the containers first (docker compose up -d): {ex.Message}");
    return 1;
}

try
{
    PortAudio.Initialize();
}
catch (Exception ex) when (ex is PortAudioException or DllNotFoundException)
{
    Console.Error.WriteLine($"Audio is unavailable: {ex.Message}");
    return 1;
}

try
{
    // PortAudio lists devices once, at Initialize. A device plugged in later is not seen
    // until the next run.
    AudioDevices devices = AudioDevices.Detect();
    if (devices.CannotStart(typed) is string reason)
    {
        Console.Error.WriteLine(reason);
        return 1;
    }
    if (!devices.HasOutput) Console.WriteLine("No speakers or headphones found: replies are printed, not spoken.");

    VoiceAgent agent = new(new AnthropicClient(), repo);
    Console.WriteLine(typed ? "Type a request, empty line to quit." : "Enter to talk, Enter again to stop. Ctrl+C to quit.");
    bool alreadyPressed = false;

    while (true)
    {
        string heard;
        if (typed)
        {
            Console.Write("\n> ");
            heard = Console.ReadLine() ?? "";
            if (heard.Length == 0) break;
        }
        else
        {
            if (!alreadyPressed)
            {
                Console.Write("\n[Enter to talk] ");
                Console.ReadLine();
            }
            alreadyPressed = false;
            Console.Write("listening... ");
            byte[] wav;
            try
            {
                wav = await Microphone.RecordAsync(ct => Task.Run(() => Console.ReadLine(), ct), CancellationToken.None);
            }
            catch (AudioDeviceException ex)
            {
                Console.WriteLine($"\n  microphone failed: {ex.Message}");
                continue;
            }

            Stopwatch stt = Stopwatch.StartNew();
            try
            {
                heard = await whisper.TranscribeAsync(wav, Vocabulary, "en", CancellationToken.None);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                Console.WriteLine($"\n  speech-to-text failed: {ex.Message}");
                continue;
            }
            // Always show what was heard: one wrong word can flip the question.
            Console.WriteLine($"\n  heard: \"{heard}\"  ({stt.ElapsedMilliseconds} ms)");
        }

        Stopwatch thinking = Stopwatch.StartNew();
        string reply;
        try
        {
            reply = await agent.AskAsync(heard, text => Console.WriteLine($"  ┌ screen\n  │ {text.Replace("\n", "\n  │ ")}\n  └"), CancellationToken.None);
        }
        catch (RefusalException)
        {
            reply = "I can't help with that one.";
        }
        catch (Exception ex) when (ex is AnthropicException or HttpRequestException or TaskCanceledException)
        {
            // Rate limits, overload, a dropped connection: say so and keep the session.
            Console.WriteLine($"  request failed: {ex.Message}");
            reply = "I couldn't reach the model. Try again in a moment.";
        }
        Console.WriteLine($"  ({thinking.ElapsedMilliseconds} ms)");
        if (reply.Length == 0) continue;

        Console.WriteLine($"  says: \"{reply}\"");
        if (!devices.HasOutput) continue;

        byte[] speech;
        try
        {
            speech = await piper.SynthesizeAsync(reply, CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.WriteLine($"  text-to-speech failed: {ex.Message}");
            continue;
        }

        if (typed)
        {
            try
            {
                await Speaker.PlayAsync(speech, CancellationToken.None);
            }
            catch (AudioDeviceException ex)
            {
                Console.WriteLine($"  playback failed: {ex.Message}");
            }
            continue;
        }

        // Enter while it speaks cuts it off (barge-in). Enter after it finishes starts the
        // next recording, so either way one key press moves the conversation on.
        using CancellationTokenSource stop = new();
        Task<string?> press = Task.Run(Console.ReadLine);
        Task playback = Speaker.PlayAsync(speech, stop.Token);
        if (await Task.WhenAny(press, playback) == press) stop.Cancel();
        try
        {
            await playback;
        }
        catch (AudioDeviceException ex)
        {
            // The reply is already printed. Fall through: the read started above still owns
            // the next Enter, so it must be awaited, or it would swallow the next key press.
            Console.WriteLine($"  playback failed: {ex.Message}");
        }
        if (!press.IsCompleted) Console.Write("  [Enter to talk] ");
        await press;
        alreadyPressed = true;
    }
}
finally
{
    PortAudio.Terminate();
}
return 0;
