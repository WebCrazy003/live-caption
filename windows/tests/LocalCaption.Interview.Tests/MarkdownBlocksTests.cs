namespace LocalCaption.Interview.Tests;

/// <summary>
/// The Answers panel's Markdown subset — block rules ported from macOS
/// <c>MarkdownText.swift</c>, inline rules standing in for Foundation's inline-only parser.
/// </summary>
public sealed class MarkdownBlocksTests
{
    private static MarkdownBlock.Paragraph P(string s) => new(s);

    [Fact]
    public void EmptyAndBlankTextHaveNoBlocks()
    {
        Assert.Empty(MarkdownBlocks.Parse(""));
        Assert.Empty(MarkdownBlocks.Parse("\n  \n\t\n"));
    }

    [Fact]
    public void ConsecutiveLinesJoinIntoOneParagraphAndBlankLinesSplit()
    {
        var blocks = MarkdownBlocks.Parse("first line\n  second line  \n\nthird");
        Assert.Equal([P("first line second line"), P("third")], blocks);
    }

    [Fact]
    public void WindowsLineEndingsAreNormalised()
    {
        Assert.Equal([P("a b"), P("c")], MarkdownBlocks.Parse("a\r\nb\r\n\r\nc"));
    }

    [Fact]
    public void HeadingsNeedASpaceAfterTheHashes()
    {
        var blocks = MarkdownBlocks.Parse("# One\n## Two  \n###### Six\n#NoSpace\n###");
        Assert.Equal(
            [
                new MarkdownBlock.Heading("One", 1),
                new MarkdownBlock.Heading("Two", 2),
                new MarkdownBlock.Heading("Six", 6),
                P("#NoSpace ###"),
            ],
            blocks);
    }

    [Fact]
    public void BulletsTakeThreeMarkersAndIndentIsSpacesOverTwo()
    {
        var blocks = MarkdownBlocks.Parse("- dash\n* star\n• dot\n    - nested\n -odd");
        Assert.Equal(
            [
                new MarkdownBlock.Bullet("dash", 0),
                new MarkdownBlock.Bullet("star", 0),
                new MarkdownBlock.Bullet("dot", 0),
                new MarkdownBlock.Bullet("nested", 2),
                P("-odd"),
            ],
            blocks);
    }

    [Fact]
    public void BoldLineIsNotABullet()
    {
        Assert.Equal([P("**Q:** Why us?")], MarkdownBlocks.Parse("**Q:** Why us?"));
    }

    [Fact]
    public void NumberedItemsAcceptDotOrParenthesis()
    {
        var blocks = MarkdownBlocks.Parse("1. one\n12) twelve\n3.no space\nv1. not\n1.");
        Assert.Equal(
            [
                new MarkdownBlock.Numbered("one", "1"),
                new MarkdownBlock.Numbered("twelve", "12"),
                P("3.no space v1. not 1."),
            ],
            blocks);
    }

    [Fact]
    public void ABlockLineEndsTheParagraphBeforeIt()
    {
        var blocks = MarkdownBlocks.Parse("intro text\n- item\nafter");
        Assert.Equal([P("intro text"), new MarkdownBlock.Bullet("item", 0), P("after")], blocks);
    }

    [Fact]
    public void FencedCodeKeepsLinesVerbatim()
    {
        var blocks = MarkdownBlocks.Parse("before\n```python\n  def f():\n\n      return 1\n```\nafter");
        Assert.Equal([P("before"), new MarkdownBlock.Code("  def f():\n\n      return 1"), P("after")], blocks);
    }

    [Fact]
    public void AnUnclosedFenceStillRendersAsCode()
    {
        // What a streaming answer looks like mid-fence.
        Assert.Equal([P("x"), new MarkdownBlock.Code("let a = 1\n# not a heading")],
                     MarkdownBlocks.Parse("x\n```\nlet a = 1\n# not a heading"));
    }

    [Fact]
    public void StreamingPrefixesOnlyChangeTheLastBlock()
    {
        const string full = "## Answer\n- one\n- two\n\nClosing words here.";
        var final = MarkdownBlocks.Parse(full);
        for (var cut = 1; cut < full.Length; cut++)
        {
            var partial = MarkdownBlocks.Parse(full[..cut]);
            // Every block but the last is already final.
            for (var i = 0; i < partial.Count - 1; i++) Assert.Equal(final[i], partial[i]);
        }
    }

    [Fact]
    public void PlainInlineIsOneRun()
    {
        Assert.Equal([new MarkdownRun("just text", MarkdownStyle.None)], MarkdownBlocks.Inline("just text"));
    }

    [Fact]
    public void BoldItalicAndCode()
    {
        Assert.Equal(
            [
                new MarkdownRun("Q:", MarkdownStyle.Bold),
                new MarkdownRun(" a ", MarkdownStyle.None),
                new MarkdownRun("b", MarkdownStyle.Italic),
                new MarkdownRun(" ", MarkdownStyle.None),
                new MarkdownRun("x = 1", MarkdownStyle.Code),
                new MarkdownRun(" ", MarkdownStyle.None),
                new MarkdownRun("c", MarkdownStyle.Bold),
                new MarkdownRun(" ", MarkdownStyle.None),
                new MarkdownRun("d", MarkdownStyle.Italic),
            ],
            MarkdownBlocks.Inline("**Q:** a *b* `x = 1` __c__ _d_"));
    }

    [Fact]
    public void BoldAndItalicTogether()
    {
        Assert.Equal([new MarkdownRun("both", MarkdownStyle.Bold | MarkdownStyle.Italic)], MarkdownBlocks.Inline("***both***"));
    }

    [Fact]
    public void EmphasisNests()
    {
        Assert.Equal(
            [
                new MarkdownRun("a ", MarkdownStyle.Bold),
                new MarkdownRun("b", MarkdownStyle.Bold | MarkdownStyle.Italic),
                new MarkdownRun(" c", MarkdownStyle.Bold),
            ],
            MarkdownBlocks.Inline("**a *b* c**"));
        Assert.Equal(
            [
                new MarkdownRun("a ", MarkdownStyle.Italic),
                new MarkdownRun("b", MarkdownStyle.Bold | MarkdownStyle.Italic),
            ],
            MarkdownBlocks.Inline("*a **b***"));
    }

    [Fact]
    public void UnmatchedMarkersStayLiteral()
    {
        // A half-streamed answer shows its markers until the closer arrives.
        Assert.Equal([new MarkdownRun("**bold so far", MarkdownStyle.None)], MarkdownBlocks.Inline("**bold so far"));
        Assert.Equal([new MarkdownRun("`open code", MarkdownStyle.None)], MarkdownBlocks.Inline("`open code"));
        Assert.Equal([new MarkdownRun("5 * 3 * 2", MarkdownStyle.None)], MarkdownBlocks.Inline("5 * 3 * 2"));
    }

    [Fact]
    public void UnderscoresInsideWordsAreLiteral()
    {
        Assert.Equal([new MarkdownRun("use snake_case_names here", MarkdownStyle.None)],
                     MarkdownBlocks.Inline("use snake_case_names here"));
    }

    [Fact]
    public void CodeSpansAreLiteralAndHideMarkersFromEmphasis()
    {
        Assert.Equal(
            [new MarkdownRun("a ", MarkdownStyle.Italic), new MarkdownRun("*b*", MarkdownStyle.Italic | MarkdownStyle.Code)],
            MarkdownBlocks.Inline("*a `*b*`*"));
        Assert.Equal([new MarkdownRun("a`b", MarkdownStyle.Code)], MarkdownBlocks.Inline("``a`b``"));
        Assert.Equal([new MarkdownRun(" x ", MarkdownStyle.Code)], MarkdownBlocks.Inline("`  x  `"));
    }

    [Fact]
    public void EscapesAndEntities()
    {
        Assert.Equal([new MarkdownRun("*not italic* & <b>", MarkdownStyle.None)],
                     MarkdownBlocks.Inline(@"\*not italic\* &amp; &lt;b&gt;"));
        Assert.Equal([new MarkdownRun("AT&T stays", MarkdownStyle.None)], MarkdownBlocks.Inline("AT&T stays"));
    }

    [Fact]
    public void StrikeThrough()
    {
        Assert.Equal([new MarkdownRun("old", MarkdownStyle.Strike), new MarkdownRun(" new", MarkdownStyle.None)],
                     MarkdownBlocks.Inline("~~old~~ new"));
    }

    [Fact]
    public void LinksKeepTheirTextAndTarget()
    {
        Assert.Equal(
            [
                new MarkdownRun("see ", MarkdownStyle.None),
                new MarkdownRun("the ", MarkdownStyle.Link, "https://example.com/a_(b)"),
                new MarkdownRun("docs", MarkdownStyle.Link | MarkdownStyle.Bold, "https://example.com/a_(b)"),
                new MarkdownRun(".", MarkdownStyle.None),
            ],
            MarkdownBlocks.Inline("see [the **docs**](https://example.com/a_(b))."));
        Assert.Equal([new MarkdownRun("[not a link] (x)", MarkdownStyle.None)], MarkdownBlocks.Inline("[not a link] (x)"));
    }

    [Fact]
    public void PlainTextDropsMarkers()
    {
        Assert.Equal("Q: Why us? use x", MarkdownBlocks.PlainText("**Q:** *Why* us? use `x`"));
    }
}
