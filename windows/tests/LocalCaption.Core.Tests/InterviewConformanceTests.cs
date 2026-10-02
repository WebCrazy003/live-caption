using LocalCaption.Core.Data;
using LocalCaption.Core.Interview;

namespace LocalCaption.Core.Tests;

/// <summary>
/// Interview Assist's shared vectors (SPEC-11 §Windows compatibility contract):
/// <c>testdata/hotkey</c>, <c>testdata/ask</c> and <c>testdata/interview-prompt</c>, read
/// exactly as <c>app/Tests/LocalCaptionKitTests/InterviewConformanceTests.swift</c> reads them.
/// Also the hand-written macOS tests that cover the same three ports
/// (<c>InterviewKitTests.swift</c>), and the <c>Config.Interview</c> helpers.
/// </summary>
public class InterviewConformanceTests
{
    // ── Hotkey ───────────────────────────────────────────────────────────────────────────

    private static string Code(HotkeyParseErrorKind kind) => kind switch
    {
        HotkeyParseErrorKind.Empty => "empty",
        HotkeyParseErrorKind.UnknownModifier => "unknown_modifier",
        HotkeyParseErrorKind.UnknownKey => "unknown_key",
        HotkeyParseErrorKind.MissingKey => "missing_key",
        HotkeyParseErrorKind.NeedsModifier => "needs_modifier",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [Fact]
    public void HotkeyVectors()
    {
        var count = 0;
        foreach (var (file, v) in Vectors.Load<HotkeyVector>("hotkey"))
        {
            foreach (var c in v.Cases)
            {
                count++;
                var result = Hotkey.Parse(c.Input);
                if (result.Value is { } hk)
                {
                    Assert.True(c.Expect == hk.ToString(), $"{file}: \"{c.Input}\" canonical form: expected {c.Expect}, got {hk}");
                    Assert.True(c.Error is null, $"{file}: \"{c.Input}\" should be invalid");
                    Assert.True(Hotkey.Parse(hk.ToString()) == new HotkeyParseResult(hk, null), $"{file}: canonical form re-parses");
                }
                else
                {
                    var error = result.Error!;
                    Assert.True(c.Expect is null, $"{file}: \"{c.Input}\" should be valid, got {error}");
                    if (c.Error is { } expected)
                        Assert.True(expected == Code(error.Kind), $"{file}: \"{c.Input}\" error: expected {expected}, got {Code(error.Kind)}");
                    Assert.True(Hotkey.Resolve(c.Input) == Hotkey.Default, $"{file}: invalid resolves to F8");
                }
            }
        }
        Assert.Equal(21, count);
    }

    [Fact]
    public void ScreenshotHotkeyDefaultsToF9()
    {
        Assert.Equal("F9", Hotkey.DefaultScreenshotString);
        Assert.Equal("F9", Hotkey.DefaultScreenshot.ToString());
        Assert.Equal(Hotkey.DefaultScreenshot, Hotkey.Resolve("nonsense", Hotkey.DefaultScreenshot));
        Assert.Equal("Ctrl+Shift+S", Hotkey.Resolve("Ctrl+Shift+S", Hotkey.DefaultScreenshot).ToString());
        Assert.Equal("F8", Hotkey.DefaultString);
        Assert.Equal(Hotkey.Default, Hotkey.Resolve(""));

        Assert.Equal("F9", new Config.InterviewGroup().ScreenshotHotkey);
        var old = System.Text.Json.JsonSerializer.Deserialize<Config.InterviewGroup>("""{"hotkey":"F7"}""")!;
        Assert.Equal("F9", old.ScreenshotHotkey);     // configs written before the key get the default
        Assert.Equal("F7", old.Hotkey);
    }

    [Fact]
    public void PanelLayoutDefaultsToAutomaticAndToleratesUnknownValues()
    {
        static Config.InterviewGroup Decode(string json) =>
            System.Text.Json.JsonSerializer.Deserialize<Config.InterviewGroup>(json)!;

        Assert.Equal("automatic", new Config.InterviewGroup().PanelLayout);
        Assert.Equal("stacked", Decode("""{"panel_layout":"stacked"}""").PanelLayout);
        Assert.Equal("side_by_side", Decode("""{"panel_layout":"side_by_side"}""").PanelLayout);
        Assert.Equal("automatic", Decode("""{"panel_layout":"diagonal"}""").PanelLayout);   // a newer build's value
        var c = new Config.InterviewGroup { PanelLayout = "side_by_side" };
        Assert.Equal("side_by_side", Decode(System.Text.Json.JsonSerializer.Serialize(c)).PanelLayout);
    }

    [Fact]
    public void HotkeyFollowsSwiftStringSemantics()
    {
        // Only spaces and tabs are trimmed (CharacterSet.whitespaces), so a newline is a key name.
        Assert.Equal(new HotkeyParseError(HotkeyParseErrorKind.UnknownKey, "\n"), Hotkey.Parse("Ctrl+\n").Error);
        Assert.Equal("Ctrl+K", Hotkey.Parse("\tctrl + k ").Value?.ToString());
        Assert.Equal("Ctrl+K", Hotkey.Parse("Ctrl+K").Value?.ToString());
        Assert.Equal(new HotkeyParseError(HotkeyParseErrorKind.NeedsModifier, "Up"), Hotkey.Parse("shift+UP").Error);
        Assert.Equal(new HotkeyParseError(HotkeyParseErrorKind.MissingKey), Hotkey.Parse("+F8").Error);
        Assert.True(Hotkey.Parse("Alt+F3").Value!.IsFunctionKey);
    }

    // ── AskSelection ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void AskSelectionVectors()
    {
        var count = 0;
        foreach (var (file, v) in Vectors.Load<AskVector>("ask"))
        {
            count++;
            var i = v.Input;
            AskSelection.Mode mode = i.Mode == "last_sentences"
                ? new AskSelection.Mode.LastSentences(i.Sentences ?? 3)
                : new AskSelection.Mode.SinceLastAsk();
            var r = AskSelection.Select(
                i.Segments.Select(s => new AskSelection.Segment(s.Text, s.TStartMs, s.TEndMs)).ToList(),
                i.Interim, mode,
                i.Mark is { } m ? new AskSelection.Mark(m.AudioMs, m.InterimWasSent) : null,
                i.PressMs, i.MaxWords);
            Assert.True(v.Expect.Text == r.Text, $"{file}: text\n  expected: {v.Expect.Text}\n  actual:   {r.Text}");
            Assert.True(new AskSelection.Mark(v.Expect.Mark.AudioMs, v.Expect.Mark.InterimWasSent) == r.Mark,
                $"{file}: mark {r.Mark}");
            Assert.True(v.Expect.FromMs == r.FromMs, $"{file}: from {r.FromMs}");
            Assert.True(v.Expect.ToMs == r.ToMs, $"{file}: to {r.ToMs}");
        }
        Assert.Equal(8, count);
    }

    [Fact]
    public void LastWordsCollapsesWhitespaceAndKeepsTheEnd()
    {
        Assert.Equal("b c", AskSelection.LastWords(" a\n\tb  c ", 2));
        Assert.Equal("a b c", AskSelection.LastWords("a b c", 10));
        Assert.Equal("", AskSelection.LastWords("a b c", 0));
        Assert.Equal("", AskSelection.LastWords("a b c", -1));
        Assert.Equal("", AskSelection.LastWords("   ", 5));
    }

    [Fact]
    public void SegmentsComeFromTranscriptSegments()
    {
        var seg = new Transcripts.TranscriptSegment { Text = "Hi.", TStartMs = 10, TEndMs = 20 };
        Assert.Equal(new AskSelection.Segment("Hi.", 10, 20), new AskSelection.Segment(seg));
    }

    // ── InterviewPrompt ──────────────────────────────────────────────────────────────────

    [Fact]
    public void InterviewPromptVectors()
    {
        var count = 0;
        foreach (var (file, v) in Vectors.Load<PromptVector>("interview-prompt"))
        {
            for (var n = 0; n < v.Cases.Count; n++)
            {
                count++;
                var c = v.Cases[n];
                string actual;
                switch (c.Kind)
                {
                    case "base":
                        Assert.True(InterviewConfig.TryParseAnswerLength(c.Length ?? "", out var length),
                            $"{file} case {n}: unknown length {c.Length}");
                        actual = InterviewPrompt.BaseInstructions(length, c.Custom ?? "");
                        break;
                    case "skill":
                        actual = InterviewPrompt.SkillMessage(
                            c.Command ?? "",
                            c.Definition is { } d
                                ? new InterviewPrompt.Skill(d.Title, d.Text,
                                    (d.Files ?? []).Select(f => new InterviewPrompt.Skill.File(f.Path, f.Text)).ToList())
                                : null,
                            (c.Attachments ?? []).Select(a => new InterviewPrompt.Attachment(a.Title, a.Text)).ToList());
                        break;
                    case "ask":
                        actual = InterviewPrompt.Ask(c.Text ?? "", c.ImageCount ?? 0);
                        break;
                    case "regenerate":
                        actual = InterviewPrompt.Regenerate;
                        break;
                    case "summary":
                        actual = InterviewPrompt.Summary(c.Transcript ?? "");
                        break;
                    default:
                        Assert.Fail($"{file} case {n}: unknown kind {c.Kind}");
                        continue;
                }
                Assert.True(c.Expect == actual,
                    $"{file} case {n} ({c.Kind})\n  expected: {c.Expect}\n  actual:   {actual}");
            }
        }
        Assert.Equal(17, count);
    }

    [Fact]
    public void SummaryKeepsTheLast15000Words()
    {
        var words = string.Join(" ", Enumerable.Range(1, 15_010).Select(n => $"w{n}"));
        var msg = InterviewPrompt.Summary(words);
        Assert.DoesNotContain("w10 ", msg);
        Assert.Contains("w11 ", msg);
        Assert.Contains("w15010\n", msg);
    }

    [Fact]
    public void PromptsAreLfOnlyAndKeepTheirDashes()
    {
        foreach (var length in Enum.GetValues<AnswerLength>())
            Assert.DoesNotContain('\r', InterviewPrompt.BaseInstructions(length, "x"));
        Assert.DoesNotContain('\r', InterviewPrompt.Summary("a b"));
        Assert.Contains("2–3", InterviewPrompt.LengthRule(AnswerLength.Short));
        Assert.Contains("no new speech — see", InterviewPrompt.Ask(" ", 1));
    }

    // ── Config.Interview helpers ─────────────────────────────────────────────────────────

    [Fact]
    public void InterviewConfigClampsAndResolves()
    {
        var g = new Config.InterviewGroup();
        Assert.Equal(3, g.ClampedSendSentences);
        Assert.Equal(400, g.ClampedMaxWords);
        Assert.Equal(new AskSelection.Mode.SinceLastAsk(), g.AskMode);
        Assert.Equal(AnswerLength.Medium, g.AnswerLengthValue);
        Assert.Equal("gpt-6-luna", InterviewConfig.RecommendedModel);
        Assert.Equal("gpt-6-luna", g.EffectiveModel);

        g.SendSentences = 0; g.MaxWords = 10; g.SendMode = "last_sentences"; g.AnswerLength = "long"; g.Model = "o9";
        Assert.Equal(1, g.ClampedSendSentences);
        Assert.Equal(50, g.ClampedMaxWords);
        Assert.Equal(new AskSelection.Mode.LastSentences(1), g.AskMode);
        Assert.Equal(AnswerLength.Long, g.AnswerLengthValue);
        Assert.Equal("o9", g.EffectiveModel);

        g.SendSentences = 99; g.MaxWords = 99_999;
        Assert.Equal(20, g.ClampedSendSentences);
        Assert.Equal(2000, g.ClampedMaxWords);
        Assert.Equal(new AskSelection.Mode.LastSentences(20), g.AskMode);
    }
}

// ── Vector shapes ────────────────────────────────────────────────────────────────────────
// Mirror the Decodable structs in InterviewConformanceTests.swift; names map via SnakeCaseLower.

internal sealed record HotkeyVector(List<HotkeyCase> Cases);
internal sealed record HotkeyCase(string Input, string? Expect, string? Error);

internal sealed record AskVector(AskInput Input, AskExpect Expect);
internal sealed record AskSegment(string Text, int TStartMs, int TEndMs);
internal sealed record AskMark(int AudioMs, bool InterimWasSent);
internal sealed record AskInput(
    List<AskSegment> Segments, string Interim, string Mode, int? Sentences,
    AskMark? Mark, int PressMs, int MaxWords);
internal sealed record AskExpect(string Text, AskMark Mark, int FromMs, int ToMs);

internal sealed record PromptVector(List<PromptCase> Cases);
internal sealed record PromptSkillFile(string Path, string Text);
internal sealed record PromptDefinition(string Title, string Text, List<PromptSkillFile>? Files);
internal sealed record PromptAttachment(string Title, string Text);
internal sealed record PromptCase(
    string Kind, string? Length, string? Custom, string? Command, PromptDefinition? Definition,
    List<PromptAttachment>? Attachments, string? Text, int? ImageCount, string? Transcript, string Expect);
