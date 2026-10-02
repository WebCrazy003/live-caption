using LocalCaption.Core.Data;

namespace LocalCaption.Core.Interview;

/// <summary><c>interview.answer_length</c> (SPEC-11 §Config); the config file stores it lower-case.</summary>
public enum AnswerLength
{
    Short,
    Medium,
    Long,
}

/// <summary>
/// The derived values the Interview ports read from <see cref="Config.InterviewGroup"/>: the
/// clamps from SPEC-11 §Config, the resolved Ask mode and the model to request. Ports of the
/// <c>Config.Interview</c> extensions in <c>AskSelection.swift</c> and of
/// <c>effectiveModel</c> / <c>recommendedModel</c> in <c>Config.swift</c>.
/// </summary>
/// <remarks>
/// The group stores enum-valued keys as strings, reset to their defaults on load when a newer
/// build's value is met. A string set in code that is not a known value reads as that same
/// default here (<c>since_last_ask</c>, <c>medium</c>).
/// </remarks>
public static class InterviewConfig
{
    /// <summary>The S0-recommended model, used when <c>model</c> is empty (SPEC-12 §S0).</summary>
    public const string RecommendedModel = "gpt-6-luna";

    extension(Config.InterviewGroup interview)
    {
        /// <summary><c>send_sentences</c> clamped to 1–20.</summary>
        public int ClampedSendSentences => Math.Min(20, Math.Max(1, interview.SendSentences));

        /// <summary><c>max_words</c> clamped to 50–2000.</summary>
        public int ClampedMaxWords => Math.Min(2000, Math.Max(50, interview.MaxWords));

        /// <summary><c>send_mode</c> as an <see cref="AskSelection.Mode"/>, carrying the clamped sentence count.</summary>
        public AskSelection.Mode AskMode => interview.SendMode == "last_sentences"
            ? new AskSelection.Mode.LastSentences(interview.ClampedSendSentences)
            : new AskSelection.Mode.SinceLastAsk();

        /// <summary><c>answer_length</c> as an <see cref="Interview.AnswerLength"/>.</summary>
        public AnswerLength AnswerLengthValue =>
            TryParseAnswerLength(interview.AnswerLength, out var length) ? length : Interview.AnswerLength.Medium;

        /// <summary>The model to request: the configured one, or <see cref="RecommendedModel"/> when empty.</summary>
        public string EffectiveModel =>
            interview.Model.Length == 0 ? RecommendedModel : interview.Model;
    }

    /// <summary>
    /// The config spelling of an answer length — <c>short</c>, <c>medium</c>, <c>long</c>, exactly
    /// (Swift's <c>AnswerLength(rawValue:)</c>).
    /// </summary>
    public static bool TryParseAnswerLength(string raw, out AnswerLength length)
    {
        switch (raw)
        {
            case "short": length = Interview.AnswerLength.Short; return true;
            case "medium": length = Interview.AnswerLength.Medium; return true;
            case "long": length = Interview.AnswerLength.Long; return true;
            default: length = default; return false;
        }
    }
}
