using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using PortAudioSharp;
using PaStream = PortAudioSharp.Stream;

/// <summary>Push-to-talk capture: 16 kHz, mono, 16-bit — the format Whisper wants, so nothing resamples.</summary>
sealed class Microphone
{
    public const int SampleRate = 16_000;

    public static async Task<byte[]> RecordAsync(Func<CancellationToken, Task> waitForStop, CancellationToken ct)
    {
        // The callback runs on PortAudio's audio thread. It must never wait on anything the
        // rest of the program does, so it only copies samples into an unbounded channel,
        // whose writes never block.
        Channel<short[]> chunks = Channel.CreateUnbounded<short[]>(new() { SingleReader = true, SingleWriter = true });

        PaStream.Callback callback = (IntPtr input, IntPtr output, uint frameCount,
            ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags flags, IntPtr userData) =>
        {
            short[] chunk = new short[frameCount];
            Marshal.Copy(input, chunk, 0, (int)frameCount);
            chunks.Writer.TryWrite(chunk);
            return StreamCallbackResult.Continue;
        };

        int device = PortAudio.DefaultInputDevice;
        if (device == PortAudio.NoDevice) throw new InvalidOperationException("No input device found.");

        StreamParameters parameters = new()
        {
            device = device,
            channelCount = 1,
            sampleFormat = SampleFormat.Int16,
            suggestedLatency = PortAudio.GetDeviceInfo(device).defaultLowInputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero,
        };

        using (PaStream stream = new(parameters, null, SampleRate, 0, StreamFlags.ClipOff, callback, null!))
        {
            stream.Start();
            try
            {
                await waitForStop(ct);
            }
            finally
            {
                stream.Stop();          // blocks until the last callback has returned
                chunks.Writer.Complete();
            }
        }

        List<short> samples = [];
        await foreach (short[] chunk in chunks.Reader.ReadAllAsync(CancellationToken.None))
            samples.AddRange(chunk);

        return Wav.Encode([.. samples], SampleRate);
    }
}

/// <summary>Plays 16-bit mono PCM, and stops the moment the token is cancelled.</summary>
static class Speaker
{
    static readonly short[] Silence = new short[4096];

    public static async Task PlayAsync(byte[] wav, CancellationToken ct)
    {
        (short[] samples, int sampleRate) = Wav.Decode(wav);
        int position = 0;
        TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

        PaStream.Callback callback = (IntPtr input, IntPtr output, uint frameCount,
            ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags flags, IntPtr userData) =>
        {
            int remaining = samples.Length - position;
            int count = Math.Min(remaining, (int)frameCount);
            if (count > 0) Marshal.Copy(samples, position, output, count);

            // Pad the final buffer with silence instead of replaying stale memory,
            // without allocating on the audio thread.
            for (int padded = count; padded < frameCount; )
            {
                int n = Math.Min(Silence.Length, (int)frameCount - padded);
                Marshal.Copy(Silence, 0, output + padded * sizeof(short), n);
                padded += n;
            }

            position += count;
            return position >= samples.Length ? StreamCallbackResult.Complete : StreamCallbackResult.Continue;
        };

        int device = PortAudio.DefaultOutputDevice;
        if (device == PortAudio.NoDevice) throw new InvalidOperationException("No output device found.");

        StreamParameters parameters = new()
        {
            device = device,
            channelCount = 1,
            sampleFormat = SampleFormat.Int16,
            suggestedLatency = PortAudio.GetDeviceInfo(device).defaultHighOutputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero,
        };

        using PaStream stream = new(null, parameters, sampleRate, 0, StreamFlags.ClipOff, callback, null!);
        stream.SetFinishedCallback(_ => finished.TrySetResult());
        stream.Start();

        try
        {
            await finished.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Barge-in: drop the buffered audio instead of draining it. The finished
            // callback still fires after this; completing an already-abandoned task is harmless.
            stream.Abort();
        }
    }
}

static class Wav
{
    public static byte[] Encode(short[] samples, int sampleRate)
    {
        byte[] wav = new byte[44 + samples.Length * 2];
        Span<byte> h = wav;
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteInt32LittleEndian(h[4..], wav.Length - 8);
        "WAVEfmt "u8.CopyTo(h[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(h[16..], 16);              // fmt chunk size
        BinaryPrimitives.WriteInt16LittleEndian(h[20..], 1);               // PCM
        BinaryPrimitives.WriteInt16LittleEndian(h[22..], 1);               // mono
        BinaryPrimitives.WriteInt32LittleEndian(h[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(h[28..], sampleRate * 2);  // byte rate
        BinaryPrimitives.WriteInt16LittleEndian(h[32..], 2);               // block align
        BinaryPrimitives.WriteInt16LittleEndian(h[34..], 16);              // bits per sample
        "data"u8.CopyTo(h[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(h[40..], samples.Length * 2);
        MemoryMarshal.AsBytes(samples.AsSpan()).CopyTo(h[44..]);
        return wav;
    }

    /// <summary>Walks the chunks instead of assuming a 44-byte header: Piper's is, other writers' aren't.</summary>
    public static (short[] Samples, int SampleRate) Decode(byte[] wav)
    {
        ReadOnlySpan<byte> s = wav;
        if (!s[..4].SequenceEqual("RIFF"u8) || !s[8..12].SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Not a RIFF/WAVE file.");

        int sampleRate = 0, offset = 12;
        while (offset + 8 <= s.Length)
        {
            ReadOnlySpan<byte> id = s.Slice(offset, 4);
            // Streaming writers put 0xFFFFFFFF here ("unknown"), which reads as -1: take the rest.
            uint declared = BinaryPrimitives.ReadUInt32LittleEndian(s[(offset + 4)..]);
            int size = (int)Math.Min(declared, (uint)(s.Length - offset - 8));
            ReadOnlySpan<byte> body = s.Slice(offset + 8, size);

            if (id.SequenceEqual("fmt "u8))
            {
                if (body.Length < 16) throw new InvalidDataException("fmt chunk too short.");
                short channels = BinaryPrimitives.ReadInt16LittleEndian(body[2..]);
                short bits = BinaryPrimitives.ReadInt16LittleEndian(body[14..]);
                if (channels != 1 || bits != 16) throw new InvalidDataException($"Expected 16-bit mono, got {bits}-bit x{channels}.");
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (sampleRate == 0) throw new InvalidDataException("data chunk before fmt chunk.");
                return (MemoryMarshal.Cast<byte, short>(body).ToArray(), sampleRate);
            }
            offset += 8 + size + (size & 1);    // chunks are word-aligned
        }
        throw new InvalidDataException("No data chunk.");
    }

    public static TimeSpan Duration(byte[] wav)
    {
        (short[] samples, int rate) = Decode(wav);
        return TimeSpan.FromSeconds((double)samples.Length / rate);
    }
}
