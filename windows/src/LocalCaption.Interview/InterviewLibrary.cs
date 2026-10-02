using System.Globalization;
using LocalCaption.Core;
using LocalCaption.Core.Data;
using LocalCaption.Core.Interview;
using LocalCaption.Core.Transcripts;
using Kind = LocalCaption.Core.Interview.InterviewLibraryIndex.DocumentKind;

namespace LocalCaption.Interview;

/// <summary>
/// The user's interview library — skills and CVs (SPEC-13 §Library), stored under
/// <c>interview\library\</c> exactly as on macOS, so the folder and its <c>index.json</c> move
/// between machines. Files are copied in on import; the originals are never referenced again.
/// A port of macOS <c>InterviewLibrary.swift</c> without the UI binding: <see cref="Changed"/>
/// tells the view to re-read.
/// </summary>
public sealed class InterviewLibrary
{
    private readonly string _root;
    private string IndexPath => Path.Combine(_root, "index.json");
    private string SkillsDir => Path.Combine(_root, "skills");
    private string DocumentsDir => Path.Combine(_root, "documents");

    public InterviewLibrary(string? root = null)
    {
        _root = root ?? AppPaths.Library;
        Index = InterviewLibraryIndex.Load(IndexPath);
    }

    public InterviewLibraryIndex Index { get; private set; }
    public string? LastError { get; private set; }
    public event Action? Changed;

    public IReadOnlyList<InterviewLibraryIndex.Document> Documents(Kind kind) =>
        Index.Documents.Where(d => d.Kind == kind).ToList();

    public InterviewLibraryIndex.Document? Document(string id) => Index.Documents.FirstOrDefault(d => d.Id == id);
    public InterviewLibraryIndex.Skill? Skill(string id) => Index.Skills.FirstOrDefault(s => s.Id == id);

    /// <summary>The newest skill with this slug (SPEC-13 §Skill steps finds steps by slug).</summary>
    public InterviewLibraryIndex.Skill? SkillBySlug(string slug) => Index.Skills.LastOrDefault(s => s.Slug == slug);

    private void Save()
    {
        try { Index.Write(IndexPath); LastError = null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LastError = $"Could not save the library: {e.Message}";
        }
        Changed?.Invoke();
    }

    // ── Documents ────────────────────────────────────────────────────────────────────────

    /// <summary>Import a <c>.pdf</c>, <c>.md</c> or <c>.txt</c> file.</summary>
    /// <exception cref="DocumentText.Failure">The file cannot be turned into text.</exception>
    /// <exception cref="IOException">The library folder cannot be written.</exception>
    public InterviewLibraryIndex.Document ImportDocument(string path, Kind kind)
    {
        var text = DocumentText.Extract(path);
        var title = Path.GetFileNameWithoutExtension(path);
        var slug = LibrarySlug.Make(title, Taken(DocumentsDir, Index.Documents.Select(d => d.Slug)));
        var dir = Path.Combine(DocumentsDir, slug);
        var original = "original" + Path.GetExtension(path).ToLowerInvariant();
        try
        {
            Directory.CreateDirectory(dir);
            File.Copy(path, Path.Combine(dir, original));
            Files.WriteAllTextAtomic(Path.Combine(dir, "text.txt"), text);
        }
        catch
        {
            TryDelete(dir);
            throw;
        }

        var doc = new InterviewLibraryIndex.Document
        {
            Slug = slug, Kind = kind, Title = title, Original = original,
            Chars = Characters(text), AddedAt = TimeFormat.Iso(DateTimeOffset.Now),
        };
        Index.Documents.Add(doc);
        Save();
        return doc;
    }

    /// <summary>The text that is actually sent (<c>text.txt</c>).</summary>
    public string Text(string id)
    {
        return Document(id) is { } d ? ReadOrEmpty(Path.Combine(DocumentsDir, d.Slug, "text.txt")) : "";
    }

    // ── Skills ───────────────────────────────────────────────────────────────────────────

    /// <param name="Ignored">Files left out: Codex can't run or read them under the lockdown (SPEC-13).</param>
    public sealed record SkillImport(InterviewLibraryIndex.Skill Skill, IReadOnlyList<string> Ignored);

    /// <summary>Why a skill could not be imported, worded for the user.</summary>
    public sealed class LibraryError(string message) : Exception(message);

    /// <summary>Import a folder containing <c>SKILL.md</c>, or a single <c>.md</c> file.</summary>
    /// <exception cref="LibraryError">Missing, no <c>SKILL.md</c>, or not Markdown.</exception>
    /// <exception cref="DocumentText.Failure">A file in it cannot be read.</exception>
    /// <exception cref="IOException">The library folder cannot be written.</exception>
    public SkillImport ImportSkill(string path) => Import(path, slot: null);

    /// <summary>
    /// Settings → Skills: load a <c>.md</c> file or a skill folder into a fixed slot
    /// (<c>discovery-cv</c>, <c>discovery-jd</c>, <c>apply-instruction</c>,
    /// <c>live-coding-design</c>), replacing what was there. The slot name becomes the slug, so
    /// steps find it whatever the file was called.
    /// </summary>
    public SkillImport LoadSkill(string slot, string path) => Import(path, slot);

    public void RemoveSkill(string slot)
    {
        TryDelete(Path.Combine(SkillsDir, slot));
        Index.Skills.RemoveAll(s => s.Slug == slot);
        Save();
    }

    /// <summary>The skill's definition as a skill step sends it (SPEC-13 §Skill message).</summary>
    public InterviewPrompt.Skill? PromptSkill(string id)
    {
        if (Skill(id) is not { } s) return null;
        var dir = Path.Combine(SkillsDir, s.Slug);
        var files = s.Files.Where(f => f != SkillFile.MainName)
            .Select(f => new InterviewPrompt.Skill.File(f, ReadOrEmpty(Path.Combine(dir, f))))
            .ToList();
        return new InterviewPrompt.Skill(s.Title, ReadOrEmpty(Path.Combine(dir, SkillFile.MainName)), files);
    }

    /// <summary>
    /// Read a skill, then write it under <paramref name="slot"/> — replacing whatever had that
    /// slug — or under a fresh slug from its title. Everything is read before anything is
    /// touched, and the index is saved once at the end.
    /// </summary>
    private SkillImport Import(string path, string? slot)
    {
        string main;
        var refs = new List<(string Path, string Text)>();
        IReadOnlyList<string> ignored = [];
        var fallbackTitle = Path.GetFileNameWithoutExtension(path);

        if (Directory.Exists(path))
        {
            var skillFile = Path.Combine(path, SkillFile.MainName);
            if (!File.Exists(skillFile)) throw new LibraryError("That folder has no SKILL.md.");
            main = DocumentText.ReadText(skillFile);
            var parts = SkillFile.Partition(RelativeFiles(path));
            ignored = parts.Ignored;
            refs = parts.Included.Select(r => (r, DocumentText.ReadText(Path.Combine(path, r)))).ToList();
            fallbackTitle = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        }
        else if (File.Exists(path))
        {
            if (!Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase))
                throw new LibraryError("A single-file skill must be a .md file.");
            main = DocumentText.ReadText(path);
        }
        else
        {
            throw new LibraryError("That file no longer exists.");
        }

        var title = SkillFile.FrontMatter(main).Name is { Length: > 0 } name ? name : fallbackTitle;
        var slug = slot ?? LibrarySlug.Make(title, Taken(SkillsDir, Index.Skills.Select(s => s.Slug)));
        var dir = Path.Combine(SkillsDir, slug);
        TryDelete(dir);
        try
        {
            Directory.CreateDirectory(dir);
            Files.WriteAllTextAtomic(Path.Combine(dir, SkillFile.MainName), main);
            foreach (var (rel, text) in refs)
                Files.WriteAllTextAtomic(Path.Combine(dir, rel), text);
        }
        catch
        {
            TryDelete(dir);
            throw;
        }

        var skill = new InterviewLibraryIndex.Skill
        {
            Slug = slug, Title = title,
            Files = [SkillFile.MainName, .. refs.Select(r => r.Path)],
            Chars = Characters(main) + refs.Sum(r => Characters(r.Text)),
            AddedAt = TimeFormat.Iso(DateTimeOffset.Now),
        };
        Index.Skills.RemoveAll(s => s.Slug == slug);
        Index.Skills.Add(skill);
        Save();
        return new SkillImport(skill, ignored);
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────

    /// <summary>Swift's <c>String.count</c>: user-perceived characters, as <c>index.json</c> records.</summary>
    private static int Characters(string text) => new StringInfo(text).LengthInTextElements;

    /// <summary>
    /// Every regular file under <paramref name="dir"/>, relative and <c>/</c>-separated as
    /// <c>index.json</c> stores them on macOS. Links are skipped, so a skill cannot reach outside
    /// its folder or loop.
    /// </summary>
    private static List<string> RelativeFiles(string dir) =>
        Directory.EnumerateFiles(dir, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            })
            .Select(f => Path.GetRelativePath(dir, f).Replace(Path.DirectorySeparatorChar, '/'))
            .ToList();

    /// <summary>Slugs in the index plus folders already on disk — one left by a failed import included.</summary>
    private static HashSet<string> Taken(string dir, IEnumerable<string> indexed)
    {
        var taken = indexed.ToHashSet();
        if (Directory.Exists(dir))
            taken.UnionWith(Directory.EnumerateDirectories(dir).Select(d => Path.GetFileName(d)!));
        return taken;
    }

    private static string ReadOrEmpty(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
