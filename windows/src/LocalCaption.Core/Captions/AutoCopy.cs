using LocalCaption.Core.Data;

namespace LocalCaption.Core.Captions;

/// <summary>What asked for "the last N sentences" to go on the clipboard.</summary>
public enum CopyTrigger
{
    /// <summary>The Copy button, the menu or the shortcut: always copies.</summary>
    Manual,
    /// <summary>A speech endpoint, with the latest interim — only with Auto-copy on.</summary>
    SpeechEnded,
    /// <summary>A final replaced its provisional — only with Auto-copy on.</summary>
    Finalized,
}

/// <summary>
/// The clipboard decision behind "Copy last N" and Auto-copy (specs/SPEC-16 §2.5), kept pure
/// so it is tested on every platform.
/// </summary>
/// <remarks>
/// Windows once used "an empty interim" to mean "the Copy button", but a final arrives with
/// the hypothesis usually empty — so with Auto-copy off it still wrote the clipboard after
/// most finals (SPEC-WINDOWS §12.1). The trigger is now said, not inferred, as the Mac does
/// (<c>LC/Session/SessionController.swift</c>: <c>copyLastN()</c> and the guarded
/// <c>onSpeechEnded</c> / <c>onFinalized</c>).
/// </remarks>
public static class AutoCopy
{
    /// <summary>
    /// The text to put on the clipboard, or null to leave the clipboard alone.
    /// </summary>
    /// <param name="committed">Every committed final so far, joined by spaces.</param>
    /// <param name="interim">The provisional text at an endpoint; ignored for a manual copy.</param>
    public static string? TextFor(Config config, CopyTrigger trigger, string committed, string interim = "")
    {
        if (trigger != CopyTrigger.Manual && !config.Clipboard.AutoUpdate) return null;

        var text = Sentences.LastN(committed, trigger == CopyTrigger.Manual ? "" : interim,
                                   config.Clipboard.RecentSentences);
        return text.Length == 0 ? null : text;
    }
}
