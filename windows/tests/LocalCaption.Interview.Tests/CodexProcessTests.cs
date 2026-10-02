using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using LocalCaption.Core.Interview;

namespace LocalCaption.Interview.Tests;

/// <summary>The process side: npm shim resolution, the line transport over a real child, log rotation.</summary>
public sealed class CodexProcessTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "lc-proc-" + Guid.NewGuid().ToString("N"));

    public CodexProcessTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private const string NpmShimText = """
        @ECHO off
        GOTO start
        :find_dp0
        SET dp0=%~dp0
        EXIT /b
        :start
        SETLOCAL
        CALL :find_dp0

        IF EXIST "%dp0%\node.exe" (
          SET "_prog=%dp0%\node.exe"
        ) ELSE (
          SET "_prog=node"
          SET PATHEXT=%PATHEXT:;.JS;=;%
        )

        endLocal & goto #_undefined_# 2>NUL || title %COMSPEC% & "%_prog%"  "%dp0%\node_modules\@openai\codex\bin\codex.js" %*
        """;

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([_tmp, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    private string Shim()
    {
        var shim = Path.Combine(_tmp, "codex.cmd");
        File.WriteAllText(shim, NpmShimText);
        return shim;
    }

    [Fact]
    public void ShimResolvesToTheNativeExeForThisArchitecture()
    {
        var shim = Shim();
        Touch("node_modules", "@openai", "codex", "bin", "codex.js");
        Touch("node_modules", "@openai", "codex", "node_modules", "@openai", "codex-win32-arm64", "vendor",
              "aarch64-pc-windows-msvc", "codex", "codex.exe");
        var x64 = Touch("node_modules", "@openai", "codex", "node_modules", "@openai", "codex-win32-x64", "vendor",
                        "x86_64-pc-windows-msvc", "codex", "codex.exe");
        var command = NpmShim.Resolve(shim, Architecture.X64, () => "node.exe");
        Assert.Equal(x64, command.FileName);
        Assert.Empty(command.PrefixArguments);
        Assert.Equal(shim, command.Located);
    }

    [Fact]
    public void ShimFindsAHoistedPlatformPackage()
    {
        var shim = Shim();
        Touch("node_modules", "@openai", "codex", "bin", "codex.js");
        var exe = Touch("node_modules", "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "codex", "codex.exe");
        Assert.Equal(exe, NpmShim.Resolve(shim, Architecture.X64, () => null).FileName);
    }

    [Fact]
    public void ShimSkipsANativeExeBuiltForAnotherArchitecture()
    {
        var shim = Shim();
        var entry = Touch("node_modules", "@openai", "codex", "bin", "codex.js");
        Touch("node_modules", "@openai", "codex", "node_modules", "@openai", "codex-win32-arm64", "vendor",
              "aarch64-pc-windows-msvc", "codex", "codex.exe");
        var command = NpmShim.Resolve(shim, Architecture.X64, () => @"C:\node\node.exe");
        Assert.Equal(@"C:\node\node.exe", command.FileName); // node's entry script picks the right binary
        Assert.Equal([entry], command.PrefixArguments);

        // The platform package name alone identifies the architecture too.
        var arm = Touch("hoisted", "@openai", "codex-win32-arm64", "bin", "codex.exe");
        Assert.Equal(arm, NpmShim.NativeExecutable(Path.Combine(_tmp, "hoisted", "@openai"), Architecture.Arm64));
        Assert.Null(NpmShim.NativeExecutable(Path.Combine(_tmp, "hoisted", "@openai"), Architecture.X64));
    }

    [Fact]
    public void ShimWithoutExeRunsNodeOnTheEntryScript()
    {
        var shim = Shim();
        var entry = Touch("node_modules", "@openai", "codex", "bin", "codex.js");
        var command = NpmShim.Resolve(shim, Architecture.X64, () => @"C:\node\node.exe");
        Assert.Equal(@"C:\node\node.exe", command.FileName);
        Assert.Equal([entry, "app-server"], command.Arguments(["app-server"]));

        var bundled = Touch("node.exe"); // npm's own shim prefers a node.exe beside it
        Assert.Equal(bundled, NpmShim.Resolve(shim, Architecture.X64, () => @"C:\node\node.exe").FileName);
    }

    [Fact]
    public void ShimFallsBackToCmdOnlyWhenNothingResolves()
    {
        var shim = Shim();
        var command = NpmShim.Resolve(shim, Architecture.X64, () => null);
        Assert.Equal(["/c", shim], command.PrefixArguments);
        Assert.Contains("cmd", command.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("last resort", command.Via);

        Touch("node_modules", "@openai", "codex", "bin", "codex.js"); // entry but no node anywhere
        Assert.Equal(["/c", shim], NpmShim.Resolve(shim, Architecture.X64, () => null).PrefixArguments);
    }

    [Fact]
    public void LocatorEnvironmentSetsCodexHomeAndPutsTheCodexFolderFirstOnPath()
    {
        var command = CodexCommand.Direct(Path.Combine(_tmp, "bin", "codex"));
        var env = CodexLocator.Environment(command, "/x/codex-home");
        Assert.Equal("/x/codex-home", env["CODEX_HOME"]);
        Assert.StartsWith(Path.Combine(_tmp, "bin") + Path.PathSeparator, env["PATH"]);
        Assert.Equal(Environment.GetEnvironmentVariable("HOME"), env.GetValueOrDefault("HOME"));
    }

    [Fact]
    public void ConfiguredPathComesFirst()
    {
        var candidates = CodexLocator.Candidates("  ~/tools/codex ");
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "tools/codex"), candidates[0]);
        if (OperatingSystem.IsWindows())
            Assert.EndsWith(@"npm\codex.cmd", candidates[1]);
        else
            Assert.Equal("/opt/homebrew/bin/codex", candidates[1]);
    }

    [Fact]
    public void LocatorRejectsOldBuildsAndAcceptsSupportedOnes()
    {
        if (OperatingSystem.IsWindows()) return; // uses sh scripts as fake codex binaries
        string FakeCodex(string name, string version)
        {
            var path = Path.Combine(_tmp, name);
            File.WriteAllText(path, $"#!/bin/sh\necho 'codex-cli {version}'\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return path;
        }
        var old = FakeCodex("old", "0.158.0");
        Assert.Equal("0.158.0", CodexLocator.Run(old, ["--version"], null)?.Trim()[^7..]);
        Assert.False(CodexLocator.Locate(old) is LocateResult.Found f && f.Command.FileName == old);
        var good = FakeCodex("good", "0.160.1");
        var found = Assert.IsType<LocateResult.Found>(CodexLocator.Locate(good));
        Assert.Equal(good, found.Command.FileName);
        Assert.Equal("0.160.1", found.VersionString);
    }

    [Fact]
    public async Task TransportSpeaksNewlineDelimitedUtf8AndLogsStderr()
    {
        if (OperatingSystem.IsWindows()) return; // drives /bin/sh
        var log = Path.Combine(_tmp, "logs", "codex.log");
        var t = new ProcessLineTransport("/bin/sh", ["-c", "echo started >&2; cat"], _tmp,
            CodexLocator.Environment(CodexCommand.Direct("/bin/sh"), _tmp), log);
        var line = JsonValue.Obj(("text", "naïve – “quoted” 日本"), ("path", "a/b")).Line();
        t.Send(line);
        t.Send("second");
        var received = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reading = Task.Run(async () =>
        {
            await foreach (var l in t.Lines.WithCancellation(cts.Token)) received.Add(l);
        });
        await WaitFor(() => received.Count == 2);
        t.Terminate(); // closes stdin: cat exits, the stream ends
        await reading;
        Assert.Equal([line, "second"], received);
        Assert.Throws<EngineException>(() => t.Send("late"));
        await WaitFor(() => File.Exists(log) && File.ReadAllText(log).Contains("started"));
        Assert.Equal("started\n", File.ReadAllText(log));
    }

    [Fact]
    public async Task TransportWritesNoByteOrderMarkAndLfOnly()
    {
        if (OperatingSystem.IsWindows()) return; // drives /bin/sh
        var t = new ProcessLineTransport("/bin/sh", ["-c", "od -An -tx1 | tr -d ' \\n'; echo"], _tmp,
            CodexLocator.Environment(CodexCommand.Direct("/bin/sh"), _tmp), null);
        t.Send("é");
        t.Terminate();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var lines = new List<string>();
        await foreach (var l in t.Lines.WithCancellation(cts.Token)) lines.Add(l);
        Assert.Equal(["c3a90a"], lines); // é + \n, no EF BB BF, no \r
    }

    [Fact]
    public async Task TerminateKillsAChildThatKeepsRunningAndReleasesIt()
    {
        if (OperatingSystem.IsWindows()) return; // drives /bin/sh
        // Ignores stdin closing: only the kill 2 s after Terminate ends it.
        var t = new ProcessLineTransport("/bin/sh", ["-c", "echo up; exec sleep 30"], _tmp,
            CodexLocator.Environment(CodexCommand.Direct("/bin/sh"), _tmp), null);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var lines = new List<string>();
        var reading = Task.Run(async () =>
        {
            await foreach (var l in t.Lines.WithCancellation(cts.Token)) lines.Add(l);
        });
        await WaitFor(() => lines.Count == 1);
        t.Terminate();
        t.Terminate(); // idempotent
        await reading; // the stream ends once the child is killed
        Assert.Equal(["up"], lines);
        Assert.Throws<EngineException>(() => t.Send("late")); // the Process is released: Crashed, not ObjectDisposed
    }

    [Fact]
    public void LogRotatesAtFiveMegabytes()
    {
        var log = Path.Combine(_tmp, "codex.log");
        File.WriteAllBytes(log, new byte[CodexProcessLog.MaxBytes + 1]);
        File.WriteAllText(log + ".1", "older");
        using (var stream = CodexProcessLog.Open(log)!)
            stream.Write(Encoding.UTF8.GetBytes("fresh"));
        Assert.Equal("fresh", File.ReadAllText(log));
        Assert.Equal(CodexProcessLog.MaxBytes + 1, new FileInfo(log + ".1").Length);

        using (var stream = CodexProcessLog.Open(log)!)
            stream.Write(Encoding.UTF8.GetBytes("+more")); // under the limit: appended
        Assert.Equal("fresh+more", File.ReadAllText(log));
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition());
    }
}

/// <summary>The user-facing strings: status lines, error lines, usage line.</summary>
public sealed class EngineTextTests
{
    [Fact]
    public void StatusSummariesUseThePlatformsInstallHints()
    {
        Assert.Equal("Codex isn't installed. Install it with npm: npm install -g @openai/codex",
            new EngineStatus.NotInstalled().SummaryFor(windows: true));
        Assert.Equal("Codex isn't installed. Install it with Homebrew: brew install codex",
            new EngineStatus.NotInstalled().SummaryFor(windows: false));
        Assert.Equal("Codex 0.150.0 is too old — LocalCaption needs 0.159.3 or newer (npm update -g @openai/codex).",
            new EngineStatus.TooOld("0.150.0").SummaryFor(windows: true));
        Assert.Equal("Codex 0.150.0 is too old — LocalCaption needs 0.159.3 or newer (brew upgrade codex).",
            new EngineStatus.TooOld("0.150.0").SummaryFor(windows: false));
        Assert.Equal("Not signed in to ChatGPT. Sign in here or in Settings → Codex.", new EngineStatus.SignedOut().Summary);
        Assert.Equal("Signed in as me@example.com (Plus)", new EngineStatus.Ready("me@example.com", "plus").Summary);
        Assert.Equal("Signed in as ChatGPT", new EngineStatus.Ready(null, null).Summary);
        Assert.Equal("Codex failed to start: boom", new EngineStatus.Failed("boom").Summary);
    }

    [Fact]
    public void ErrorMessagesMatchTheMac()
    {
        Assert.Equal("Codex isn't installed.", new EngineError.NotInstalled().Message);
        Assert.Equal("Codex 0.1 is too old.", new EngineError.TooOld("0.1").Message);
        Assert.Equal("Not signed in to ChatGPT.", new EngineError.SignedOut().Message);
        Assert.Equal("Still answering the previous question.", new EngineError.Busy().Message);
        Assert.Equal("No answer after 2 minutes.", new EngineError.Timeout().Message);
        Assert.Equal("Codex restarted — press Ask again.", new EngineError.Crashed().Message);
        Assert.Equal("Plus usage limit reached.", new EngineError.UsageLimit(null).Message);
        Assert.StartsWith("Plus limit reached — resets at ", new EngineError.UsageLimit(DateTimeOffset.UnixEpoch).Message);
        Assert.Equal("Network problem: down", new EngineError.Network("down").Message);
        Assert.Equal("Blocked: the model tried to use a tool (commandExecution).", new EngineError.BlockedTool("commandExecution").Message);
        Assert.Equal("x", new EngineError.Rpc("x").Message);
        Assert.Equal("y", new EngineError.Other("y").Message);
        Assert.Equal("No answer after 2 minutes.", new EngineException(new EngineError.Timeout()).Message);
    }

    [Fact]
    public void UsageLineShowsTimeTodayAndWeekdayOtherwise()
    {
        var zone = TimeZoneInfo.Utc;
        var culture = CultureInfo.GetCultureInfo("en-GB");
        var now = new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
        var today = new CodexRpc.Usage.Window(300, 18, new DateTimeOffset(2026, 10, 2, 14, 20, 0, TimeSpan.Zero).ToUnixTimeSeconds());
        Assert.Equal("5-hour: 82% left · resets 14:20", UsageLine.For(today, now, zone, culture));
        var later = new CodexRpc.Usage.Window(10080, 40, new DateTimeOffset(2026, 10, 6, 8, 5, 0, TimeSpan.Zero).ToUnixTimeSeconds());
        Assert.Equal("Weekly: 60% left · resets Tue 08:05", UsageLine.For(later, now, zone, culture));
        Assert.Equal("Usage: 100% left", UsageLine.For(new CodexRpc.Usage.Window(null, 0, null), now, zone, culture));
    }
}
