namespace LocalCaption.Core.Captions;

/// <summary>
/// LocalAgreement-2 interim stabilisation (SPEC.md §8.2): commit only the tokens agreed by
/// the two most recent hypotheses and keep the rest provisional, so the committed prefix of
/// the live (dimmed) line never flickers backward. Reset per utterance.
/// </summary>
/// <remarks>
/// Requires a <b>fixed</b> audio origin. Moving windows use <see cref="RollingCaption"/>.
/// </remarks>
public sealed class LocalAgreement
{
    private List<string> _previous = [];
    private List<string> _committed = [];

    public IReadOnlyList<string> Committed => _committed;

    public void Reset()
    {
        _previous = [];
        _committed = [];
    }

    /// <summary>
    /// Feed the latest hypothesis. The committed prefix grows to the common prefix of the
    /// last two hypotheses and is monotonic — it never shrinks, even if the newest
    /// hypothesis is shorter or diverges.
    /// </summary>
    public (IReadOnlyList<string> Committed, IReadOnlyList<string> Provisional) Update(
        IReadOnlyList<string> hypothesis)
    {
        var agreed = CommonPrefix(_previous, hypothesis);
        if (agreed.Count > _committed.Count) _committed = agreed;
        _previous = [.. hypothesis];

        // The provisional tail is measured from the COMMITTED length, not from the length
        // of the previous hypothesis.
        var tail = hypothesis.Count > _committed.Count
            ? hypothesis.Skip(_committed.Count).ToList()
            : [];
        return (_committed, tail);
    }

    internal static List<string> CommonPrefix(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var out_ = new List<string>();
        var n = Math.Min(a.Count, b.Count);
        for (var i = 0; i < n && a[i] == b[i]; i++) out_.Add(a[i]);
        return out_;
    }
}
