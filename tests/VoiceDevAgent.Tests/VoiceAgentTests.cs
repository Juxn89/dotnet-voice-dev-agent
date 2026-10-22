public class IsBlankTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[BLANK_AUDIO]")]
    [InlineData("(silence)")]
    [InlineData(" [inaudible] . ")]
    [InlineData("[Music] (wind blowing)")]
    [InlineData(">>")]
    public void Silence_markers_alone_are_blank(string transcript) =>
        Assert.True(VoiceAgent.IsBlank(transcript));

    [Theory]
    [InlineData("Why is the build red?")]
    [InlineData("[BLANK_AUDIO] run the tests")]
    [InlineData("ok")]
    public void Any_spoken_word_is_a_request(string transcript) =>
        Assert.False(VoiceAgent.IsBlank(transcript));
}

public class GuardSpokenTests
{
    static string Words(int count, string word = "word") => string.Join(' ', Enumerable.Repeat(word, count));

    [Fact]
    public void Short_reply_is_spoken_as_is_and_nothing_goes_to_screen()
    {
        List<string> screen = [];
        string reply = "Build's red, three errors, all from your rename.";

        Assert.Equal(reply, VoiceAgent.GuardSpoken(reply, screen.Add));
        Assert.Empty(screen);
    }

    [Fact]
    public void Exactly_the_limit_is_still_spoken()
    {
        List<string> screen = [];
        string reply = Words(30);

        Assert.Equal(reply, VoiceAgent.GuardSpoken(reply, screen.Add));
        Assert.Empty(screen);
    }

    [Fact]
    public void Long_reply_goes_to_screen_and_only_the_first_sentence_is_spoken()
    {
        List<string> screen = [];
        string reply = "Build's red. " + Words(40) + ".";

        string spoken = VoiceAgent.GuardSpoken(reply, screen.Add);

        Assert.Equal("Build's red. The rest is on screen.", spoken);
        Assert.Equal(new[] { reply }, screen);
    }

    [Fact]
    public void A_dot_inside_a_file_name_or_number_does_not_end_the_sentence()
    {
        string reply = "The error is in ChangedFile.cs after 1.8 seconds. " + Words(40) + ".";

        string spoken = VoiceAgent.GuardSpoken(reply, _ => { });

        Assert.Equal("The error is in ChangedFile.cs after 1.8 seconds. The rest is on screen.", spoken);
    }

    [Fact]
    public void A_first_sentence_over_the_limit_is_cut_at_the_limit()
    {
        string spoken = VoiceAgent.GuardSpoken(Words(40), _ => { });

        Assert.Equal(Words(30) + "... The rest is on screen.", spoken);
    }
}
