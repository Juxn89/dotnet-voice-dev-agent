public class AudioDevicesTests
{
    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, false, true)]
    public void Speakers_are_optional(bool hasInput, bool hasOutput, bool typed) =>
        Assert.Null(new AudioDevices(hasInput, hasOutput).CannotStart(typed));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Talking_without_a_microphone_cannot_start_and_points_to_text_mode(bool hasOutput) =>
        Assert.Contains("--text", new AudioDevices(HasInput: false, hasOutput).CannotStart(typed: false));
}
