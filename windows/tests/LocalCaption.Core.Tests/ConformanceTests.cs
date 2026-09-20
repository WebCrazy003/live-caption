using System.Text.Json;
using LocalCaption.Core.Audio;
using LocalCaption.Core.Captions;

namespace LocalCaption.Core.Tests;

/// <summary>
/// Runs the shared vectors in <c>testdata/</c> against the Windows implementation. The macOS
/// suite runs the same files against <c>LocalCaptionKit</c>; a disagreement here means this
/// port is wrong (SPEC-WINDOWS.md §6.1).
/// </summary>
public class ConformanceTests
{
    [Fact]
    public void RollingCaptionVectors()
    {
        foreach (var (name, v) in Vectors.Load<RollingVector>("rolling"))
        {
            var caption = new RollingCaption();
            for (var i = 0; i < v.Steps.Count; i++)
            {
                var step = v.Steps[i];
                var incoming = step.Words.Select(w => new CaptionWord(w.Text, w.Start, w.End)).ToList();
                var accepted = caption.Update(incoming, step.StartSample, step.EndSample);
                Assert.True(step.ExpectAccepted == accepted,
                    $"{name} step {i}: expected accepted={step.ExpectAccepted}, got {accepted}");
            }
            Assert.True(v.ExpectText == caption.Text,
                $"{name}: merged text\n  expected: {v.ExpectText}\n  actual:   {caption.Text}");
        }
    }

    [Fact]
    public void SegmenterVectors()
    {
        foreach (var (name, v) in Vectors.Load<SegmenterVector>("segmenter"))
        {
            var tuning = new SpeechSegmenter.Tuning(
                v.Tuning.EndpointMs, v.Tuning.IntervalMs, v.Tuning.MaxUtteranceS, v.Tuning.Threshold);
            var segmenter = new SpeechSegmenter(Guid.NewGuid(), tuning);
            var totalFinalSamples = 0;

            for (var i = 0; i < v.Steps.Count; i++)
            {
                var step = v.Steps[i];
                var where = $"{name} step {i} ({step.Op})";
                IReadOnlyList<SpeechRequest> produced;

                switch (step.Op)
                {
                    case "append":
                        var samples = new List<float>();
                        foreach (var run in step.Runs ?? [])
                            samples.AddRange(Enumerable.Repeat(run.Amplitude, run.Count));
                        produced = segmenter.Append(samples, step.Now ?? 0);
                        break;
                    case "finish":
                        produced = segmenter.Finish(step.Now ?? 0);
                        break;
                    case "drain_transitions":
                        var actual = segmenter.DrainTransitions()
                            .Select(t => new VectorTransition(t.Utterance, t.Sample, t.Started)).ToList();
                        Assert.True((step.ExpectTransitions ?? []).SequenceEqual(actual),
                            $"{where}: transitions\n  expected: {Show(step.ExpectTransitions ?? [])}" +
                            $"\n  actual:   {Show(actual)}");
                        continue;
                    default:
                        throw new InvalidOperationException($"{where}: unknown op");
                }

                totalFinalSamples += produced.Where(r => r.IsFinal).Sum(r => r.Audio.Count);

                if (step.ExpectRequests is { } expectedAll)
                    AssertRequests(expectedAll, produced, $"{where}: requests");

                if (step.ExpectFinalRequests is { } expectedFinals)
                    AssertRequests(expectedFinals, produced.Where(r => r.IsFinal).ToList(), $"{where}: final requests");

                if (step.ExpectFinalCount is { } expectedCount)
                    Assert.True(expectedCount == produced.Count(r => r.IsFinal),
                        $"{where}: final count, expected {expectedCount}, got {produced.Count(r => r.IsFinal)}");

                if (step.ExpectInterimMaxSamples is { } cap)
                {
                    var widest = produced.Where(r => !r.IsFinal).Select(r => r.Audio.Count).DefaultIfEmpty(0).Max();
                    Assert.True(widest <= cap, $"{where}: interim window {widest} exceeds cap {cap}");
                }
            }

            if (v.ExpectTotalFinalSamples is { } expectedTotal)
                Assert.True(expectedTotal == totalFinalSamples,
                    $"{name}: every speech sample must survive — expected {expectedTotal}, got {totalFinalSamples}");
        }
    }

    private static void AssertRequests(List<VectorRequest> expected, IReadOnlyList<SpeechRequest> produced, string where)
    {
        var actual = produced
            .Select(r => new VectorRequest(r.Utterance, r.StartSample, r.Audio.Count, r.EndSample, r.IsFinal))
            .ToList();
        Assert.True(expected.SequenceEqual(actual),
            $"{where}\n  expected: {Show(expected)}\n  actual:   {Show(actual)}");
    }

    private static string Show<T>(IEnumerable<T> items) =>
        items.Any() ? string.Join(", ", items) : "(none)";

    [Fact]
    public void LocalAgreementVectors()
    {
        foreach (var (name, v) in Vectors.Load<AgreementVector>("localagreement"))
        {
            var la = new LocalAgreement();
            for (var i = 0; i < v.Steps.Count; i++)
            {
                var step = v.Steps[i];
                switch (step.Op)
                {
                    case "reset":
                        la.Reset();
                        break;
                    case "update":
                        var (committed, provisional) = la.Update(step.Hypothesis ?? []);
                        Assert.True((step.ExpectCommitted ?? []).SequenceEqual(committed),
                            $"{name} step {i}: committed\n  expected: {Show(step.ExpectCommitted ?? [])}" +
                            $"\n  actual:   {Show(committed)}");
                        Assert.True((step.ExpectProvisional ?? []).SequenceEqual(provisional),
                            $"{name} step {i}: provisional\n  expected: {Show(step.ExpectProvisional ?? [])}" +
                            $"\n  actual:   {Show(provisional)}");
                        break;
                    default:
                        throw new InvalidOperationException($"{name} step {i}: unknown op {step.Op}");
                }
            }
        }
    }

    [Fact]
    public void FilterVectors()
    {
        foreach (var (name, v) in Vectors.Load<FilterVector>("filters"))
        {
            for (var i = 0; i < v.Cases.Count; i++)
            {
                var c = v.Cases[i];
                var input = c.Input ?? "";
                var where = $"{name} case {i} ({input})";
                switch (v.Kind)
                {
                    case "clean":
                        Assert.True(c.Expect.GetString() == Filters.Clean(input),
                            $"{where}: clean → '{Filters.Clean(input)}', expected '{c.Expect.GetString()}'");
                        break;
                    case "hallucination":
                        Assert.True(c.Expect.GetBoolean() == Filters.IsHallucination(input),
                            $"{where}: isHallucination → {Filters.IsHallucination(input)}");
                        break;
                    case "low_quality":
                        var verdict = Filters.IsLowQuality(
                            c.AvgLogprob ?? 0, c.NoSpeechProb ?? 0, c.CompressionRatio ?? 0);
                        Assert.True(c.Expect.GetBoolean() == verdict,
                            $"{name} case {i}: isLowQuality(logprob={c.AvgLogprob}, " +
                            $"noSpeech={c.NoSpeechProb}, compression={c.CompressionRatio}) → {verdict}");
                        break;
                    case "counts":
                        Assert.True(c.ExpectWords == Filters.WordCount(input),
                            $"{where}: wordCount → {Filters.WordCount(input)}");
                        Assert.True(c.ExpectSentences == Filters.SentenceCount(input),
                            $"{where}: sentenceCount → {Filters.SentenceCount(input)}");
                        break;
                    default:
                        throw new InvalidOperationException($"{where}: unknown kind {v.Kind}");
                }
            }
        }
    }

    [Fact]
    public void SentenceVectors()
    {
        foreach (var (name, v) in Vectors.Load<SentenceVector>("sentences"))
        {
            for (var i = 0; i < v.Cases.Count; i++)
            {
                var c = v.Cases[i];
                var where = $"{name} case {i} ('{c.Text}')";
                switch (v.Kind)
                {
                    case "split":
                        var expected = c.Expect.EnumerateArray().Select(e => e.GetString()).ToList();
                        var actual = Sentences.Split(c.Text);
                        Assert.True(expected.SequenceEqual(actual!),
                            $"{where}: split\n  expected: {Show(expected)}\n  actual:   {Show(actual)}");
                        break;
                    case "last_n":
                        var lastN = Sentences.LastN(c.Text, c.N ?? 0);
                        Assert.True(c.Expect.GetString() == lastN,
                            $"{where}: lastN({c.N}) → '{lastN}', expected '{c.Expect.GetString()}'");
                        break;
                    case "last_n_appending":
                        var appended = Sentences.LastN(c.Text, c.Provisional ?? "", c.N ?? 0);
                        Assert.True(c.Expect.GetString() == appended,
                            $"{where}: lastN(+'{c.Provisional}', {c.N}) → '{appended}', " +
                            $"expected '{c.Expect.GetString()}'");
                        break;
                    default:
                        throw new InvalidOperationException($"{where}: unknown kind {v.Kind}");
                }
            }
        }
    }
}
