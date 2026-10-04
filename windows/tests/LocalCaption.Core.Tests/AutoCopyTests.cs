using LocalCaption.Core.Captions;
using LocalCaption.Core.Data;

namespace LocalCaption.Core.Tests;

/// <summary>
/// specs/SPEC-16 §2.5: Auto-copy obeys its toggle. With it off the clipboard is the user's —
/// nothing is written at a speech endpoint or a final — while the Copy button always works.
/// </summary>
public sealed class AutoCopyTests
{
    private const string Committed = "One. Two. Three. Four.";

    private static Config With(bool autoUpdate, int n = 2)
    {
        var config = new Config();
        config.Clipboard.AutoUpdate = autoUpdate;
        config.Clipboard.RecentSentences = n;
        return config;
    }

    [Theory]
    [InlineData(CopyTrigger.SpeechEnded, "Five")]
    [InlineData(CopyTrigger.SpeechEnded, "")]
    [InlineData(CopyTrigger.Finalized, "")]       // the case the old "empty interim means manual" rule got wrong
    [InlineData(CopyTrigger.Finalized, "Five")]
    public void Off_never_writes_on_speech_end_or_final(CopyTrigger trigger, string interim)
    {
        Assert.Null(AutoCopy.TextFor(With(autoUpdate: false), trigger, Committed, interim));
    }

    [Fact]
    public void Off_by_default()
    {
        Assert.Null(AutoCopy.TextFor(new Config(), CopyTrigger.Finalized, Committed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Manual_copy_always_writes_the_last_n(bool autoUpdate)
    {
        Assert.Equal("Three. Four.", AutoCopy.TextFor(With(autoUpdate), CopyTrigger.Manual, Committed));
    }

    [Fact]
    public void Manual_copy_ignores_the_interim()
    {
        Assert.Equal("Three. Four.", AutoCopy.TextFor(With(false), CopyTrigger.Manual, Committed, "Five"));
    }

    [Fact]
    public void On_writes_the_last_n_after_a_final()
    {
        Assert.Equal("Three. Four.", AutoCopy.TextFor(With(true), CopyTrigger.Finalized, Committed));
    }

    [Fact]
    public void On_includes_the_interim_at_a_speech_end()
    {
        Assert.Equal("Four. Five", AutoCopy.TextFor(With(true), CopyTrigger.SpeechEnded, Committed, "Five"));
    }

    [Fact]
    public void On_respects_n()
    {
        Assert.Equal("Two. Three. Four.", AutoCopy.TextFor(With(true, n: 3), CopyTrigger.Finalized, Committed));
    }

    [Theory]
    [InlineData(CopyTrigger.Manual)]
    [InlineData(CopyTrigger.Finalized)]
    [InlineData(CopyTrigger.SpeechEnded)]
    public void Nothing_said_writes_nothing(CopyTrigger trigger)
    {
        Assert.Null(AutoCopy.TextFor(With(true), trigger, "", ""));
    }
}
