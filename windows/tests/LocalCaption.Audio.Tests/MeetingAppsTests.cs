using LocalCaption.Audio;
using Xunit;

namespace LocalCaption.Audio.Tests;

/// <summary>
/// <c>auto</c> capture guesses which program is the call. A wrong guess records silence, so
/// the rule under test is mostly about when it must refuse to guess.
/// </summary>
public sealed class MeetingAppsTests
{
    private static AudioSource App(string executable, bool playing) => new(1, executable, executable, playing);

    [Theory]
    [InlineData("ms-teams", SourceKind.Meeting)]
    [InlineData("Zoom.exe", SourceKind.Meeting)]
    [InlineData("chrome", SourceKind.Browser)]              // Google Meet is a tab, not a program
    [InlineData("MSEDGE", SourceKind.Browser)]
    [InlineData("vmware-vmx", SourceKind.VirtualMachine)]   // a call taken inside a VM
    [InlineData("VirtualBoxVM", SourceKind.VirtualMachine)]
    [InlineData("vmconnect", SourceKind.VirtualMachine)]
    [InlineData("Spotify", SourceKind.Other)]
    public void Programs_are_recognised_without_regard_to_case_or_extension(string executable, SourceKind kind) =>
        Assert.Equal(kind, MeetingApps.Classify(executable));

    [Fact]
    public void One_recognised_program_playing_is_the_call()
    {
        var call = MeetingApps.TheOnePlaying([App("Spotify", true), App("vmware-vmx", true), App("chrome", false)]);
        Assert.Equal("vmware-vmx", call?.Executable);
    }

    [Fact]
    public void Two_candidates_playing_is_not_a_guess_worth_making()
    {
        // A meeting in Teams and a video in Chrome: either could be "the call". Capturing the
        // whole device gets both; capturing the wrong one gets forty minutes of nothing.
        Assert.Null(MeetingApps.TheOnePlaying([App("ms-teams", true), App("chrome", true)]));
    }

    [Fact]
    public void Nothing_recognised_playing_means_no_guess()
    {
        Assert.Null(MeetingApps.TheOnePlaying([App("Spotify", true), App("chrome", false)]));
        Assert.Null(MeetingApps.TheOnePlaying([]));
    }
}
