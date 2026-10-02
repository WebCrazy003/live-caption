using LocalCaption.Asr;
using Xunit;

namespace LocalCaption.Asr.Tests;

/// <summary>
/// <c>asr.vocabulary</c> → the initial prompt. The rule that matters most is the first one:
/// with nothing entered, nothing about decoding changes.
/// </summary>
public sealed class VocabularyPromptTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" , ;\n, ")]
    public void No_vocabulary_means_no_prompt(string? vocabulary) =>
        Assert.Null(WhisperEngine.BuildPrompt(vocabulary));

    [Fact]
    public void Terms_are_trimmed_deduplicated_and_framed_as_a_sentence()
    {
        var prompt = WhisperEngine.BuildPrompt("  Chukwuemeka ,Ngozi;\nAdaeze, ngozi ,, Kubernetes ");

        Assert.Equal("Names and terms: Chukwuemeka, Ngozi, Adaeze, Kubernetes.", prompt);
    }

    [Fact]
    public void A_very_long_list_is_cut_to_what_the_decoder_will_actually_keep()
    {
        var prompt = WhisperEngine.BuildPrompt(string.Join(", ", Enumerable.Range(0, 400).Select(i => $"Term{i}")));

        Assert.NotNull(prompt);
        Assert.True(prompt!.Length <= 640, $"prompt was {prompt.Length} characters");
    }

    [Fact]
    public void The_engine_carries_the_prompt_it_was_given()
    {
        Assert.Null(new WhisperEngine("tiny.en", "tiny.en").Prompt);
        Assert.Equal("Names and terms: Obinna.", new WhisperEngine("tiny.en", "tiny.en", vocabulary: "Obinna").Prompt);
    }
}
