using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

/// <summary>whisper.cpp's whisper-server: POST /inference, multipart, returns {"text": "..."}.</summary>
sealed class WhisperClient(HttpClient http)
{
    public async Task<string> TranscribeAsync(byte[] wav, string? vocabulary, string? language, CancellationToken ct)
    {
        using MultipartFormDataContent form = new();
        ByteArrayContent file = new(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "speech.wav");
        form.Add(new StringContent("json"), "response_format");
        form.Add(new StringContent("0.0"), "temperature");
        if (vocabulary is not null) form.Add(new StringContent(vocabulary), "prompt");
        if (language is not null) form.Add(new StringContent(language), "language");

        using HttpResponseMessage response = await http.PostAsync("/inference", form, ct);
        response.EnsureSuccessStatusCode();
        WhisperResult result = await response.Content.ReadFromJsonAsync<WhisperResult>(ct)
            ?? throw new InvalidDataException("Empty response from whisper-server.");
        return result.Text.Trim();
    }

    sealed record WhisperResult([property: JsonPropertyName("text")] string Text);
}

/// <summary>Piper's HTTP server: POST /synthesize with {"text": "..."}, returns a WAV.</summary>
sealed class PiperClient(HttpClient http)
{
    public async Task<byte[]> SynthesizeAsync(string text, CancellationToken ct)
    {
        using HttpResponseMessage response = await http.PostAsJsonAsync("/synthesize", new { text }, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }
}
