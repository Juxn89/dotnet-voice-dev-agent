using System.Buffers.Binary;
using System.Text;

public class WavTests
{
    /// <summary>A RIFF/WAVE file assembled from raw chunks, to test what other writers produce.</summary>
    static byte[] Riff(params (string Id, byte[] Body, uint? DeclaredSize)[] chunks)
    {
        using MemoryStream ms = new();
        ms.Write("RIFF"u8);
        ms.Write(new byte[4]);
        ms.Write("WAVE"u8);
        foreach ((string id, byte[] body, uint? declared) in chunks)
        {
            ms.Write(Encoding.ASCII.GetBytes(id));
            byte[] size = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(size, declared ?? (uint)body.Length);
            ms.Write(size);
            ms.Write(body);
            if (body.Length % 2 == 1) ms.WriteByte(0);
        }
        return ms.ToArray();
    }

    static byte[] Fmt(short channels, int sampleRate, short bits)
    {
        byte[] fmt = new byte[16];
        BinaryPrimitives.WriteInt16LittleEndian(fmt, 1);
        BinaryPrimitives.WriteInt16LittleEndian(fmt.AsSpan(2), channels);
        BinaryPrimitives.WriteInt32LittleEndian(fmt.AsSpan(4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(fmt.AsSpan(8), sampleRate * channels * bits / 8);
        BinaryPrimitives.WriteInt16LittleEndian(fmt.AsSpan(12), (short)(channels * bits / 8));
        BinaryPrimitives.WriteInt16LittleEndian(fmt.AsSpan(14), bits);
        return fmt;
    }

    static byte[] Pcm(params short[] samples)
    {
        byte[] data = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), samples[i]);
        return data;
    }

    [Fact]
    public void Encode_then_decode_round_trips()
    {
        short[] samples = [0, 1, -1, short.MaxValue, short.MinValue];

        byte[] wav = Wav.Encode(samples, 16_000);
        (short[] decoded, int rate) = Wav.Decode(wav);

        Assert.Equal(44 + samples.Length * 2, wav.Length);
        Assert.Equal(samples, decoded);
        Assert.Equal(16_000, rate);
    }

    [Fact]
    public void Duration_is_samples_over_rate() =>
        Assert.Equal(TimeSpan.FromSeconds(0.5), Wav.Duration(Wav.Encode(new short[8_000], 16_000)));

    [Fact]
    public void Skips_extra_chunks_including_odd_sized_ones()
    {
        byte[] wav = Riff(("fmt ", Fmt(1, 22_050, 16), null), ("LIST", new byte[3], null), ("data", Pcm(7, -7), null));

        (short[] samples, int rate) = Wav.Decode(wav);

        Assert.Equal(new short[] { 7, -7 }, samples);
        Assert.Equal(22_050, rate);
    }

    [Fact]
    public void An_unknown_streaming_data_size_takes_the_rest_of_the_file()
    {
        byte[] wav = Riff(("fmt ", Fmt(1, 16_000, 16), null), ("data", Pcm(1, 2, 3), 0xFFFFFFFF));

        Assert.Equal(new short[] { 1, 2, 3 }, Wav.Decode(wav).Samples);
    }

    [Fact]
    public void Rejects_stereo() =>
        Assert.Throws<InvalidDataException>(() => Wav.Decode(Riff(("fmt ", Fmt(2, 16_000, 16), null), ("data", Pcm(1, 2), null))));

    [Fact]
    public void Rejects_data_before_fmt() =>
        Assert.Throws<InvalidDataException>(() => Wav.Decode(Riff(("data", Pcm(1), null), ("fmt ", Fmt(1, 16_000, 16), null))));

    [Fact]
    public void Rejects_a_file_with_no_data_chunk() =>
        Assert.Throws<InvalidDataException>(() => Wav.Decode(Riff(("fmt ", Fmt(1, 16_000, 16), null))));

    [Fact]
    public void Rejects_non_riff_input() =>
        Assert.Throws<InvalidDataException>(() => Wav.Decode(Encoding.ASCII.GetBytes("ID3 not a wave file")));
}
