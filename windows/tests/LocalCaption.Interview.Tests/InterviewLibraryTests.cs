using System.Text;
using LocalCaption.Core.Interview;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Kind = LocalCaption.Core.Interview.InterviewLibraryIndex.DocumentKind;

namespace LocalCaption.Interview.Tests;

/// <summary>
/// The CV and skill library (SPEC-16 §4.5): text extraction, the on-disk layout shared with
/// macOS, and the four fixed skill slots.
/// </summary>
public sealed class InterviewLibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"lc-library-{Guid.NewGuid():N}");
    private string Root => Path.Combine(_dir, "library");
    private string Source(string name) => Path.Combine(_dir, "in", name);

    public InterviewLibraryTests() => Directory.CreateDirectory(Path.Combine(_dir, "in"));
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    private string Write(string name, string text)
    {
        var path = Source(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private string Pdf(string name, params string?[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pages)
        {
            var page = builder.AddPage(595, 842);
            if (text is not null) page.AddText(text, 12, new UglyToad.PdfPig.Core.PdfPoint(72, 720), font);
        }
        var path = Source(name);
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    // ── DocumentText ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void PdfPagesAreTrimmedAndJoinedByABlankLine()
    {
        Assert.Equal("Jane Doe\n\nSenior engineer", DocumentText.Extract(Pdf("cv.pdf", "Jane Doe", null, "Senior engineer")));
    }

    [Fact]
    public void APdfWithNoTextIsReportedAsScanned()
    {
        var e = Assert.Throws<DocumentText.Failure>(() => DocumentText.Extract(Pdf("scan.pdf", null, null)));
        Assert.Equal("This PDF is a scanned image; paste the text instead.", e.Message);
    }

    [Fact]
    public void TextIsUtf8ElseWindows1252WithLfEndingsAndTrimmed()
    {
        Assert.Equal("café\nline two", DocumentText.Extract(Write("a.md", "  café\r\nline two\r\n\n")));

        var latin = Source("b.txt");
        File.WriteAllBytes(latin, [0x63, 0x61, 0x66, 0xE9, 0x0D, 0x93, 0x71, 0x94]);   // café\r“q” in CP1252
        Assert.Equal("café\n“q”", DocumentText.Extract(latin));
    }

    [Fact]
    public void OtherTypesAreRefusedByName()
    {
        var e = Assert.Throws<DocumentText.Failure>(() => DocumentText.Extract(Write("cv.docx", "x")));
        Assert.Equal("“.docx” files aren't supported — use PDF, Markdown or plain text, or paste the text.", e.Message);
    }

    // ── Documents ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ImportingACvCopiesTheOriginalAndItsTextAndIndexesIt()
    {
        var library = new InterviewLibrary(Root);
        var changed = 0;
        library.Changed += () => changed++;

        var doc = library.ImportDocument(Write("My CV.MD", "# Jane\n\nEngineer"), Kind.Cv);

        Assert.Equal("my-cv", doc.Slug);
        Assert.Equal("My CV", doc.Title);
        Assert.Equal("original.md", doc.Original);
        Assert.Equal(16, doc.Chars);
        Assert.True(File.Exists(Path.Combine(Root, "documents", "my-cv", "original.md")));
        Assert.Equal("# Jane\n\nEngineer", library.Text(doc.Id));
        Assert.Equal(1, changed);

        // The index on disk is the shared format: a fresh load sees the same document.
        var reloaded = new InterviewLibrary(Root);
        Assert.Equal(doc.Id, Assert.Single(reloaded.Documents(Kind.Cv)).Id);

        var second = library.ImportDocument(Write("my-cv.txt", "Other"), Kind.Cv);
        Assert.Equal("my-cv-2", second.Slug);
    }

    // ── Skills ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASkillFolderKeepsItsTextFilesAndReportsTheRest()
    {
        var folder = Source("discovery");
        Write("discovery/SKILL.md", "---\nname: Discovery CV\n---\nRead the CV.");
        Write("discovery/refs/questions.md", "Q1");
        Write("discovery/notes.txt", "N");
        Write("discovery/run.py", "print()");
        Write("discovery/.hidden.md", "secret");

        var library = new InterviewLibrary(Root);
        var result = library.ImportSkill(folder);

        Assert.Equal("Discovery CV", result.Skill.Title);
        Assert.Equal("discovery-cv", result.Skill.Slug);
        Assert.Equal(["SKILL.md", "notes.txt", "refs/questions.md"], result.Skill.Files);
        Assert.Contains("run.py", result.Ignored);
        Assert.True(File.Exists(Path.Combine(Root, "skills", "discovery-cv", "refs", "questions.md")));

        var prompt = library.PromptSkill(result.Skill.Id)!;
        Assert.Equal("---\nname: Discovery CV\n---\nRead the CV.", prompt.Text);
        Assert.Equal(["notes.txt", "refs/questions.md"], prompt.Files.Select(f => f.Path));
    }

    [Fact]
    public void LoadingASlotReplacesWhatWasThereAndUsesTheSlotAsSlug()
    {
        var library = new InterviewLibrary(Root);
        library.LoadSkill("discovery-jd", Write("first.md", "# First"));
        var second = library.LoadSkill("discovery-jd", Write("Some Other Name.md", "# Second"));

        var skill = Assert.Single(library.Index.Skills);
        Assert.Equal("discovery-jd", skill.Slug);
        Assert.Equal("Some Other Name", skill.Title);
        Assert.Equal(second.Skill.Id, library.SkillBySlug("discovery-jd")!.Id);
        Assert.Equal("# Second", File.ReadAllText(Path.Combine(Root, "skills", "discovery-jd", "SKILL.md")));
        Assert.Single(Directory.GetDirectories(Path.Combine(Root, "skills")));

        library.RemoveSkill("discovery-jd");
        Assert.Empty(library.Index.Skills);
        Assert.False(Directory.Exists(Path.Combine(Root, "skills", "discovery-jd")));
    }

    [Fact]
    public void AByteOrderMarkDoesNotHideTheFrontMatter()
    {
        var path = Source("bom.md");
        File.WriteAllText(path, "---\nname: Apply Instruction\n---\nBody", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var library = new InterviewLibrary(Root);
        var changed = 0;
        library.Changed += () => changed++;
        var skill = library.LoadSkill("apply-instruction", path).Skill;

        Assert.Equal("Apply Instruction", skill.Title);
        Assert.Equal(36, skill.Chars);              // no invisible U+FEFF counted (37 with it)
        Assert.Equal(1, changed);                   // one save for one load
    }

    [Fact]
    public void AFolderLeftByAFailedImportDoesNotBlockTheNextOne()
    {
        Directory.CreateDirectory(Path.Combine(Root, "documents", "cv"));
        File.WriteAllText(Path.Combine(Root, "documents", "cv", "original.md"), "stale");

        var doc = new InterviewLibrary(Root).ImportDocument(Write("cv.md", "Fresh"), Kind.Cv);

        Assert.Equal("cv-2", doc.Slug);
    }

    [Fact]
    public void BadSkillSourcesAreRefusedWithAReason()
    {
        var library = new InterviewLibrary(Root);
        Directory.CreateDirectory(Source("empty"));
        Assert.Equal("That folder has no SKILL.md.",
                     Assert.Throws<InterviewLibrary.LibraryError>(() => library.ImportSkill(Source("empty"))).Message);
        Assert.Equal("A single-file skill must be a .md file.",
                     Assert.Throws<InterviewLibrary.LibraryError>(() => library.ImportSkill(Write("s.txt", "x"))).Message);
        Assert.Equal("That file no longer exists.",
                     Assert.Throws<InterviewLibrary.LibraryError>(() => library.ImportSkill(Source("gone.md"))).Message);
    }
}
