import Foundation

/// The prompt for the live "Key points" summary (SPEC-10). Pure/testable: no LLM here,
/// just the exact instruction + the per-block user message. The engine (app target) sends
/// these to the on-device model; `SummaryCard.parse` normalizes whatever comes back.
///
/// Design (SPEC-10 §"Why", §"Output format"): very simple English, one clear sentence that
/// uses the earlier BACKGROUND for context, do not invent, and answer in a fixed
/// `SUMMARY/TODO` shape so the parser can be strict.
public enum SummaryPrompt {
    /// System instruction. Kept extractive + "do not invent" to limit hallucination (B8):
    /// the user can't verify the summary against audio they don't follow.
    public static let system = """
    You help a non-native English speaker follow a live call. \
    You get BACKGROUND (what was said earlier) and the NEW part of the transcript. \
    Use the background only to understand the new part. \
    In very simple English (short, common words), write ONE clear sentence that says what is \
    happening now. Do NOT invent anything that is not in the text.

    For TODO: a task exists ONLY when the other person makes a direct request to you — shown by \
    words like "can you", "could you", "please", "would you", "send me", "let me know", or \
    "I need you to". If you do NOT see such a direct request to you, you MUST write exactly "-". \
    When there is one, write it as a short command to you, starting with a verb, using ONLY words \
    from the transcript. Never guess a task. Never reuse a task from another call.

    Answer ONLY in this exact format, nothing else:
    SUMMARY: <one short, clear sentence>
    TODO: <a short command starting with a verb, or "-" if the text asks nothing of you>
    """

    /// The per-block user message. `background` is the earlier transcript tail (context only);
    /// `chunk` is the new ~100-word block to summarize.
    public static func user(_ chunk: String, background: String = "") -> String {
        let trimmed = chunk.trimmingCharacters(in: .whitespacesAndNewlines)
        let bg = background.trimmingCharacters(in: .whitespacesAndNewlines)
        if bg.isEmpty {
            return "New part of transcript:\n\"\(trimmed)\"\n\nNow write the card."
        }
        return """
        Background (earlier, for context only):
        "\(bg)"

        New part of transcript:
        "\(trimmed)"

        Now write the card.
        """
    }
}
