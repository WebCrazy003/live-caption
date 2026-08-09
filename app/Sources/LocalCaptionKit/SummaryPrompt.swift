import Foundation

/// The prompt for the live "Key points" summary (SPEC-10). Pure/testable: no LLM here,
/// just the exact instruction + the per-block user message. The engine (app target) sends
/// these to the on-device model; `SummaryCard.parse` normalizes whatever comes back.
///
/// Design (SPEC-10 §"Why", §"Output format"): very simple English, lead with the ask, do
/// not invent, and answer in a fixed `MAIN/-/WANT` shape so the parser can be strict.
public enum SummaryPrompt {
    /// System instruction. Kept extractive + "do not invent" to limit hallucination (B8):
    /// the user can't verify the summary against audio they don't follow.
    public static let system = """
    You help a non-native English speaker follow a live call. \
    Read the transcript part and say, in very simple English (short words, short lines), \
    what the other person wants. Lead with the main ask. Do NOT invent anything that is \
    not in the text. If nothing important, say so.

    Answer ONLY in this exact format:
    MAIN: <one short line: the single most important ask/point>
    - <short point in simple words>
    - <short point in simple words>
    WANT: <what they want you to do, or "-" if none>
    """

    /// The per-block user message wrapping one ~100-word transcript chunk.
    public static func user(_ chunk: String) -> String {
        let trimmed = chunk.trimmingCharacters(in: .whitespacesAndNewlines)
        return "Transcript part:\n\"\(trimmed)\"\n\nNow write the card."
    }
}
