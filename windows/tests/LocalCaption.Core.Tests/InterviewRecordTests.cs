using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalCaption.Core.Interview;
using LocalCaption.Core.Transcripts;

namespace LocalCaption.Core.Tests;

/// <summary>
/// The interview record and library (specs/SPEC-16 §3): ports of the macOS
/// <c>InterviewKitTests</c> that cover <c>InterviewRecord.swift</c>, plus the shared vectors in
/// <c>testdata/library/</c> and <c>testdata/records/</c>, which the macOS
/// <c>InterviewConformanceTests</c> asserts against the same files.
/// </summary>
public sealed class InterviewRecordTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"lc-interview-{Guid.NewGuid():N}");

    public InterviewRecordTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    /// <summary>Value equality for records that hold lists (C# records compare those by reference).</summary>
    internal static string Json<T>(T value) => JsonSerializer.Serialize(value);

    // ── Record ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RecordRoundTripsWithSnakeCaseKeys()
    {
        var rec = new InterviewRecord("Acme — Senior iOS", "2026-10-01T09:00:00Z", "gpt-6-luna", "low",
            new InterviewSetup { Company = "Acme", Role = "Senior iOS", SkillIds = ["s1"], DocumentIds = ["d1"] })
        {
            ThreadId = "thr_1",
        };
        rec.Turns.Add(new InterviewTurn
        {
            N = 1, Kind = InterviewTurnKind.Ask, Question = "Tell me about yourself", AudioFromMs = 0,
            AudioToMs = 5_000, Images = ["attachments/1-1.png"], Answer = "**Q:** …",
            Status = InterviewTurnStatus.Completed, AskedAt = "2026-10-01T09:05:00Z", TtftMs = 1_300, TotalMs = 3_600,
        });

        var json = JsonSerializer.Serialize(rec);
        var back = JsonSerializer.Deserialize<InterviewRecord>(json)!;
        Assert.Equal(json, Json(back));
        foreach (var key in new[] { "\"schema_version\"", "\"thread_id\"", "\"audio_from_ms\"", "\"ttft_ms\"",
                                    "\"skill_ids\"", "\"extra_turns\"", "\"completed\"", "\"ask\"" })
            Assert.True(json.Contains(key), $"missing {key}");
        Assert.DoesNotContain("\"session_id\"", json);   // nil is omitted, as Swift's Codable does
        Assert.Matches("^[0-9A-F-]{36}$", rec.Id);       // uppercase, as UUID().uuidString

        // Keys are written in Swift's sorted order, so the two builds' files diff cleanly.
        var keys = JsonNode.Parse(json)!.AsObject().Select(kv => kv.Key).ToList();
        Assert.Equal(keys.OrderBy(k => k, StringComparer.Ordinal), keys);
        var turnKeys = JsonNode.Parse(json)!["turns"]![0]!.AsObject().Select(kv => kv.Key).ToList();
        Assert.Equal(turnKeys.OrderBy(k => k, StringComparer.Ordinal), turnKeys);
    }

    [Fact]
    public void RecordReadsAMacInterviewJson()
    {
        // As macOS's InterviewFiles.write produces it (sorted keys, nil omitted).
        const string mac = """
            {
              "created_at" : "2026-10-01T09:00:00Z", "engine" : "codex", "id" : "ABC", "model" : "m",
              "name" : "n", "prep" : { "briefing" : "", "extra_turns" : 0, "status" : "pending" },
              "reasoning_effort" : "low", "schema_version" : 1,
              "setup" : { "answer_length" : "short", "company" : "Acme", "document_ids" : [ ], "instructions" : "",
                          "role" : "", "skill_ids" : [ "s1" ], "step" : 2 },
              "summary" : { "status" : "running" },
              "turns" : [ { "answer" : "", "asked_at" : "t", "images" : [ ], "kind" : "skill", "n" : 1,
                            "question" : "/discovery-cv", "status" : "streaming" } ]
            }
            """;
        var r = JsonSerializer.Deserialize<InterviewRecord>(mac)!;
        Assert.Equal("ABC", r.Id);
        Assert.Equal(2, r.Setup.Step);
        Assert.Equal("short", r.Setup.AnswerLength);
        Assert.Equal(InterviewStatus.Running, r.Summary.Status);
        Assert.Equal(InterviewTurnKind.Skill, r.Turns[0].Kind);
        Assert.Equal(InterviewTurnStatus.Streaming, r.Turns[0].Status);
    }

    [Fact]
    public void SessionNameIsIntervieweeCompanyStepDate()
    {
        var date = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);
        var day = TimeFormat.Day(date);
        Assert.Equal($"victor-Peloton-1-{day}", InterviewRecord.SessionName("victor", "Peloton", 1, date));
        Assert.Equal($"victor-capital on tap-2-{day}",
                     InterviewRecord.SessionName(" victor ", "capital on tap", 2, date));   // trimmed, inner spaces kept
        Assert.Equal($"victor-1-{day}", InterviewRecord.SessionName("victor", "", 1, date)); // empty parts skipped
        Assert.Null(InterviewRecord.SessionName("", "  ", 1, date));

        var rec = new InterviewRecord("x", "t", "m", "low",
                                      new InterviewSetup { Candidate = "Jane", Company = "Acme", Step = 3 });
        Assert.Equal($"Jane-Acme-3-{day}", rec.SessionName(date));
    }

    [Fact]
    public void DayIsLocal()
    {
        var local = new DateTimeOffset(new DateTime(2026, 10, 2, 0, 30, 0, DateTimeKind.Local));
        Assert.Equal("2026-10-02", TimeFormat.Day(local));
        Assert.Equal("2026-10-02", TimeFormat.Day(local.ToUniversalTime()));   // same instant, any offset
    }

    [Fact]
    public void InterruptedTurnsFailOnRelaunch()
    {
        var rec = new InterviewRecord("x", "t", "m", "low", new InterviewSetup());
        rec.Prep.Status = InterviewStatus.Running;
        rec.Turns =
        [
            new() { N = 1, Kind = InterviewTurnKind.Ask, Question = "a", Status = InterviewTurnStatus.Completed, AskedAt = "t" },
            new() { N = 2, Kind = InterviewTurnKind.Ask, Question = "b", Status = InterviewTurnStatus.Streaming, AskedAt = "t" },
        ];
        Assert.True(rec.FailInterruptedTurns());
        Assert.Equal([InterviewTurnStatus.Completed, InterviewTurnStatus.Failed], rec.Turns.Select(t => t.Status));
        Assert.Equal("app closed", rec.Turns[1].Error);
        Assert.Equal(InterviewStatus.Failed, rec.Prep.Status);
        Assert.Equal(3, rec.NextTurnNumber);
        Assert.False(rec.FailInterruptedTurns());
    }

    [Fact]
    public void SkillTurnsDriveProfileAndLiveCoding()
    {
        static InterviewTurn Skill(int n, string q, InterviewTurnStatus s) =>
            new() { N = n, Kind = InterviewTurnKind.Skill, Question = q, Status = s, AskedAt = "t" };

        var rec = new InterviewRecord("x", "t", "m", "low", new InterviewSetup());
        Assert.Equal(1, rec.NextTurnNumber);
        Assert.Null(rec.ActiveProfile);
        Assert.False(rec.LiveCodingActive);

        rec.Turns =
        [
            Skill(1, "/discovery-cv", InterviewTurnStatus.Completed),
            Skill(2, "/apply-instruction tech", InterviewTurnStatus.Completed),
            Skill(3, "/live-coding-design", InterviewTurnStatus.Interrupted),
            Skill(4, "/apply-instruction behavioural", InterviewTurnStatus.Failed),
        ];
        Assert.Equal(["apply-instruction", "discovery-cv", "live-coding-design"], rec.SkillsReceived.Order());
        Assert.Equal("tech", rec.ActiveProfile);
        Assert.False(rec.LiveCodingActive);   // interrupted does not count

        rec.Turns.Add(Skill(5, "/live-coding-design", InterviewTurnStatus.Completed));
        Assert.True(rec.LiveCodingActive);
        rec.Turns.Add(Skill(6, "/apply-instruction product", InterviewTurnStatus.Completed));
        Assert.False(rec.LiveCodingActive);
        Assert.Equal("product", rec.ActiveProfile);
        Assert.Equal(7, rec.NextTurnNumber);

        Assert.Equal("apply-instruction", InterviewRecord.SkillName("/apply-instruction tech"));
        Assert.Null(InterviewRecord.SkillName("apply-instruction"));
        Assert.Null(InterviewRecord.SkillName("/"));
    }

    [Fact]
    public void QaMarkdownExport()
    {
        var rec = new InterviewRecord("Acme — iOS", "t", "m", "low", new InterviewSetup { Company = "Acme", Role = "iOS" });
        rec.Turns =
        [
            new() { N = 1, Kind = InterviewTurnKind.Ask, Question = "why us", AudioToMs = 65_000, Images = ["attachments/1-1.png"],
                    Answer = "**Q:** Why us?\nBecause.", Status = InterviewTurnStatus.Completed, AskedAt = "t" },
            new() { N = 2, Kind = InterviewTurnKind.Typed, Question = "shorter", Answer = "", Status = InterviewTurnStatus.Failed,
                    Error = "offline", AskedAt = "t" },
        ];
        Assert.Equal("""
            # Acme — iOS

            ## 1. [00:01:05] why us

            _1 screenshot(s)_

            **Q:** Why us?
            Because.

            ## 2. You: shorter

            _(no answer)_

            _(failed: offline)_
            """, rec.QaMarkdown());
    }

    // ── Library ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Slugs()
    {
        Assert.Equal("cv-2026-final", LibrarySlug.Make("CV 2026 — Final!"));
        Assert.Equal("resume", LibrarySlug.Make("Résumé"));
        Assert.Equal("item", LibrarySlug.Make("***"));
        Assert.Equal("cv-3", LibrarySlug.Make("cv", new HashSet<string> { "cv", "cv-2" }));
    }

    [Fact]
    public void LibraryIndexPreservesUnknownKeysAndRepairsCorruption()
    {
        var path = Path.Combine(_dir, "library", "index.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"schema_version":1,"documents":[],"skills":[],"future_key":42}""");

        var idx = InterviewLibraryIndex.Load(path);
        idx.Documents.Add(new InterviewLibraryIndex.Document
        {
            Slug = "cv", Kind = InterviewLibraryIndex.DocumentKind.Cv, Title = "CV", Original = "original.pdf",
            Chars = 10, AddedAt = "t",
        });
        idx.Write(path);

        var raw = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(42, (int)raw["future_key"]!);
        Assert.Equal(["documents", "future_key", "schema_version", "skills"], raw.Select(kv => kv.Key));
        Assert.Equal("cv", (string)raw["documents"]![0]!["kind"]!);
        Assert.Equal(["cv"], InterviewLibraryIndex.Load(path).Documents.Select(d => d.Slug));

        File.WriteAllText(path, "{ not json");
        Assert.Equal(Json(new InterviewLibraryIndex()), Json(InterviewLibraryIndex.Load(path)));
        var backups = Directory.GetFiles(Path.GetDirectoryName(path)!, "index.json.bak-*");
        Assert.Single(backups);
        Assert.Matches(@"index\.json\.bak-\d{8}-\d{6}$", backups[0]);
        Assert.Equal("{ not json", File.ReadAllText(backups[0]));
    }

    [Fact]
    public void LibraryIndexDecodesLikeSwift()
    {
        var path = Path.Combine(_dir, "index.json");
        Assert.Empty(InterviewLibraryIndex.Load(path).Documents);   // missing file → empty, no backup
        Assert.Empty(Directory.GetFiles(_dir));

        // Every top-level key is optional…
        File.WriteAllText(path, """{"skills":[{"id":"S","slug":"coach","title":"Coach","files":["a.md"],"chars":5,"added_at":"t"}]}""");
        var idx = InterviewLibraryIndex.Load(path);
        Assert.Equal(1, idx.SchemaVersion);
        Assert.Empty(idx.Documents);
        Assert.Equal(["a.md"], idx.Skills.Single().Files);

        // …but a document missing a field, or with an unknown kind, makes the file corrupt.
        foreach (var bad in new[]
                 {
                     """{"documents":[{"id":"D","slug":"cv","kind":"cv","title":"CV","original":"o","chars":1}]}""",
                     """{"documents":[{"id":"D","slug":"cv","kind":"pdf","title":"CV","original":"o","chars":1,"added_at":"t"}]}""",
                     """{"documents":[{"id":"D","slug":null,"kind":"cv","title":"CV","original":"o","chars":1,"added_at":"t"}]}""",
                     """[]""",
                 })
        {
            File.WriteAllText(path, bad);
            Assert.Equal(Json(new InterviewLibraryIndex()), Json(InterviewLibraryIndex.Load(path)));
        }
    }

    [Fact]
    public void SkillFrontMatterAndFilePartition()
    {
        var fm = SkillFile.FrontMatter("---\nname: \"Interview coach\"\ndescription: STAR answers\n---\n# Body");
        Assert.Equal("Interview coach", fm.Name);
        Assert.Equal("STAR answers", fm.Description);
        Assert.Null(SkillFile.FrontMatter("# No front matter").Name);

        var (included, ignored) = SkillFile.Partition(["SKILL.md", "z.txt", "refs/a.md", "run.sh", ".hidden/x.md", "img.png"]);
        Assert.Equal(["refs/a.md", "z.txt"], included);
        Assert.Equal(["img.png", "run.sh"], ignored);
    }

    // ── Shared vectors (specs/SPEC-16 §3.1) ──────────────────────────────────────────────

    [Fact]
    public void LibraryVectors()
    {
        var cases = 0;
        foreach (var (file, v) in Vectors.Load<LibraryVector>("library"))
        {
            for (var n = 0; n < v.Cases.Count; n++, cases++)
            {
                var c = v.Cases[n];
                var at = $"{file} case {n}";
                switch (v.Kind, c.Op)
                {
                    case ("slug", _):
                        var actual = LibrarySlug.Make(c.Title!, (c.Taken ?? []).ToHashSet());
                        Assert.True(c.Expect == actual, $"{at}: \"{c.Title}\" → expected {c.Expect}, got {actual}");
                        break;
                    case ("skill_files", "front_matter"):
                        var fm = SkillFile.FrontMatter(c.Text!);
                        Assert.True(c.ExpectName == fm.Name, $"{at}: name {fm.Name ?? "null"}");
                        Assert.True(c.ExpectDescription == fm.Description, $"{at}: description {fm.Description ?? "null"}");
                        break;
                    case ("skill_files", "partition"):
                        var (included, ignored) = SkillFile.Partition(c.Paths!);
                        Assert.True(c.ExpectIncluded!.SequenceEqual(included), $"{at}: included [{string.Join(", ", included)}]");
                        Assert.True(c.ExpectIgnored!.SequenceEqual(ignored), $"{at}: ignored [{string.Join(", ", ignored)}]");
                        break;
                    default:
                        Assert.Fail($"{at}: unknown kind {v.Kind} / op {c.Op}");
                        break;
                }
            }
        }
        Assert.True(cases > 0);
    }

    [Fact]
    public void RecordVectors()
    {
        var cases = 0;
        foreach (var (file, v) in Vectors.Load<RecordVector>("records"))
        {
            for (var n = 0; n < v.Cases.Count; n++, cases++)
            {
                var c = v.Cases[n];
                var at = $"{file} case {n}";
                switch (v.Kind)
                {
                    case "session_name":
                        var date = new DateTimeOffset(DateTime.ParseExact(
                            c.LocalTime!, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal));
                        var name = InterviewRecord.SessionName(c.Candidate, c.Company, c.Step, date);
                        Assert.True(c.Expect == name, $"{at}: expected {c.Expect ?? "null"}, got {name ?? "null"}");
                        break;
                    case "qa_markdown":
                        var rec = new InterviewRecord(c.Name!, "t", "m", "low",
                                                      new InterviewSetup { Company = c.Company ?? "", Role = c.Role ?? "" });
                        rec.Turns = (c.Turns ?? []).Select(t => new InterviewTurn
                        {
                            N = t.N,
                            Kind = JsonSerializer.Deserialize<InterviewTurnKind>($"\"{t.Kind}\""),
                            Question = t.Question,
                            AudioToMs = t.AudioToMs,
                            Images = t.Images ?? [],
                            Answer = t.Answer ?? "",
                            Status = JsonSerializer.Deserialize<InterviewTurnStatus>($"\"{t.Status}\""),
                            Error = t.Error,
                            AskedAt = "t",
                        }).ToList();
                        Assert.True(c.Expect == rec.QaMarkdown(),
                            $"{at}\n  expected: {c.Expect}\n  actual:   {rec.QaMarkdown()}");
                        break;
                    default:
                        Assert.Fail($"{at}: unknown kind {v.Kind}");
                        break;
                }
            }
        }
        Assert.True(cases > 0);
    }
}

// ── Vector shapes (mirror the Decodable structs in InterviewConformanceTests.swift) ──────

internal sealed record LibraryVector(string Kind, List<LibraryCase> Cases);
internal sealed record LibraryCase(
    string? Title, List<string>? Taken, string? Expect,
    string? Op, string? Text, string? ExpectName, string? ExpectDescription,
    List<string>? Paths, List<string>? ExpectIncluded, List<string>? ExpectIgnored);

internal sealed record RecordVector(string Kind, List<RecordCase> Cases);
internal sealed record RecordCase(
    string? Candidate, string? Company, int? Step, string? LocalTime,
    string? Name, string? Role, List<RecordTurn>? Turns, string? Expect);
internal sealed record RecordTurn(
    int N, string Kind, string Question, int? AudioToMs, List<string>? Images, string? Answer,
    string Status, string? Error);
