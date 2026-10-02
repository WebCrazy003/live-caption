using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using LocalCaption.Core.Interview;

namespace LocalCaption.Interview;

/// <summary>
/// How to start a located <c>codex</c>: <see cref="FileName"/> with <see cref="PrefixArguments"/>
/// before the codex arguments. Usually the binary itself; for an npm shim on Windows, the
/// package's native <c>codex.exe</c>, or <c>node &lt;entry.js&gt;</c>, or — last resort —
/// <c>cmd.exe /c codex.cmd</c> (SPEC-16 §4.1).
/// </summary>
/// <param name="Located">The candidate path that was found (the shim, for npm installs).</param>
/// <param name="Via">How it is started, for the log.</param>
public sealed record CodexCommand(string FileName, IReadOnlyList<string> PrefixArguments, string Located, string Via)
{
    /// <summary>The candidate is itself the executable.</summary>
    public static CodexCommand Direct(string path) => new(path, [], path, "direct");

    /// <summary><see cref="PrefixArguments"/> followed by <paramref name="arguments"/>.</summary>
    public IReadOnlyList<string> Arguments(IEnumerable<string> arguments) => [.. PrefixArguments, .. arguments];

    /// <inheritdoc />
    public bool Equals(CodexCommand? other) =>
        other is not null && FileName == other.FileName && PrefixArguments.SequenceEqual(other.PrefixArguments)
        && Located == other.Located && Via == other.Via;

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(FileName, PrefixArguments.Count, Located, Via);
}

/// <summary>The outcome of <see cref="CodexLocator.Locate"/>.</summary>
public abstract record LocateResult
{
    private LocateResult() { }

    /// <summary>A supported <c>codex</c>.</summary>
    public sealed record Found(CodexCommand Command, IReadOnlyList<long> Version) : LocateResult
    {
        /// <summary><c>0.159.3</c>.</summary>
        public string VersionString => string.Join('.', Version);
    }

    /// <summary>No working <c>codex</c> anywhere.</summary>
    public sealed record NotInstalled : LocateResult;

    /// <summary>Only builds older than <see cref="CodexRpc.MinimumVersion"/>; <paramref name="Version"/> is the last one seen.</summary>
    public sealed record TooOld(string Version) : LocateResult;
}

/// <summary>
/// Finds the <c>codex</c> binary and checks its version (SPEC-12 §Finding codex, SPEC-16 §4.1).
/// GUI apps don't inherit the shell <c>PATH</c>, so known locations are searched explicitly;
/// each candidate runs <c>--version</c> (5 s timeout) and the first supported one wins.
/// </summary>
public static class CodexLocator
{
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The fixed candidates, in order. Windows: <c>interview.codex_path</c>,
    /// <c>%APPDATA%\npm\codex.cmd</c>, the Volta and Scoop shims (then <c>where codex</c> in
    /// <see cref="Locate"/>). macOS/Linux: the Mac app's list (then <c>zsh -lc 'command -v codex'</c>).
    /// </summary>
    public static IReadOnlyList<string> Candidates(string configured)
    {
        var paths = new List<string>();
        var trimmed = ExpandHome(System.Environment.ExpandEnvironmentVariables(configured.Trim())).Trim();
        if (trimmed.Length > 0) paths.Add(trimmed);
        if (OperatingSystem.IsWindows())
        {
            var appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
            var localAppData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
            var profile = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
            if (appData.Length > 0) paths.Add(Path.Combine(appData, "npm", "codex.cmd"));
            if (localAppData.Length > 0) paths.Add(Path.Combine(localAppData, "Volta", "bin", "codex.exe"));
            if (profile.Length > 0)
            {
                paths.Add(Path.Combine(profile, "scoop", "shims", "codex.exe"));
                paths.Add(Path.Combine(profile, "scoop", "shims", "codex.cmd"));
            }
        }
        else
        {
            var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
            paths.AddRange(["/opt/homebrew/bin/codex", "/usr/local/bin/codex",
                            $"{home}/.npm-global/bin/codex", $"{home}/.local/bin/codex", $"{home}/.volta/bin/codex"]);
        }
        return paths;
    }

    /// <summary>Search every candidate; the first whose <c>--version</c> is supported wins.</summary>
    public static LocateResult Locate(string configured)
    {
        string? seenTooOld = null;
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var path in AllCandidates(configured))
        {
            if (!seen.Add(path) || Command(path) is not { } command) continue;
            if (Run(command.FileName, command.Arguments(["--version"]), PathEnvironment(command)) is not { } output
                || CodexRpc.Version(output) is not { } version)
                continue;
            if (CodexRpc.IsSupported(version))
            {
                InterviewLog.Write($"using codex {string.Join('.', version)} at {command.Located} ({command.Via})");
                return new LocateResult.Found(command, version);
            }
            seenTooOld = string.Join('.', version);
        }
        return seenTooOld is null ? new LocateResult.NotInstalled() : new LocateResult.TooOld(seenTooOld);
    }

    /// <summary>
    /// <see cref="Candidates"/>, then <c>where codex</c> (Windows) or the login shell's
    /// <c>command -v codex</c> — run only once every fixed candidate has failed.
    /// </summary>
    private static IEnumerable<string> AllCandidates(string configured)
    {
        foreach (var path in Candidates(configured)) yield return path;
        foreach (var path in OperatingSystem.IsWindows() ? WhereLookup() : LoginShellLookup()) yield return path;
    }

    /// <summary>
    /// The child environment: the user's, plus a <c>PATH</c> that can find <c>node</c> for npm
    /// installs, and the dedicated <c>CODEX_HOME</c> (SPEC-12 §Lockdown).
    /// </summary>
    public static IReadOnlyDictionary<string, string> Environment(CodexCommand command, string codexHome)
    {
        var env = PathEnvironment(command);
        env["CODEX_HOME"] = codexHome;
        return env;
    }

    /// <summary>The inherited environment with the codex directory (and on macOS Homebrew's) in front of <c>PATH</c>.</summary>
    private static Dictionary<string, string> PathEnvironment(CodexCommand command)
    {
        var windows = OperatingSystem.IsWindows();
        var env = new Dictionary<string, string>(windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (DictionaryEntry e in System.Environment.GetEnvironmentVariables())
            if (e.Key is string k && e.Value is string v) env[k] = v;
        var extra = new List<string>();
        if (Path.GetDirectoryName(command.Located) is { Length: > 0 } dir) extra.Add(dir);
        if (windows)
        {
            if (Path.GetDirectoryName(command.FileName) is { Length: > 0 } exeDir && !extra.Contains(exeDir)) extra.Add(exeDir);
        }
        else
        {
            extra.AddRange(["/opt/homebrew/bin", "/usr/local/bin"]);
        }
        var path = env.TryGetValue("PATH", out var p) ? p : windows ? "" : "/usr/bin:/bin";
        env["PATH"] = string.Join(Path.PathSeparator, [.. extra, path]);
        return env;
    }

    /// <summary>A candidate path → how to start it; <c>null</c> when it isn't there or can't run.</summary>
    private static CodexCommand? Command(string path)
    {
        if (!OperatingSystem.IsWindows())
            return IsExecutableFile(path) ? CodexCommand.Direct(path) : null;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext.Length == 0)
        {
            // `where` also lists the extension-less sh script npm writes for Git Bash.
            if (File.Exists(path + ".exe")) return CodexCommand.Direct(path + ".exe");
            if (File.Exists(path + ".cmd")) return NpmShim.Resolve(path + ".cmd");
            return null;
        }
        if (!File.Exists(path)) return null;
        return ext switch
        {
            ".exe" => CodexCommand.Direct(path),
            ".cmd" or ".bat" => NpmShim.Resolve(path),
            _ => null,
        };
    }

    private static bool IsExecutableFile(string path)
    {
        if (OperatingSystem.IsWindows()) return File.Exists(path);
        try
        {
            if (!File.Exists(path)) return false;
            const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(path) & anyExecute) != 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary><c>where codex</c> — every match, in <c>PATH</c> order.</summary>
    private static IEnumerable<string> WhereLookup()
    {
        var system = System.Environment.GetFolderPath(System.Environment.SpecialFolder.System);
        var where = system.Length > 0 ? Path.Combine(system, "where.exe") : "where.exe";
        if (Run(where, ["codex"], null) is not { } output) return [];
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Path.IsPathFullyQualified);
    }

    /// <summary><c>zsh -lc 'command -v codex'</c> — finds installs only the user's shell profile knows about.</summary>
    private static IEnumerable<string> LoginShellLookup()
    {
        if (Run("/bin/zsh", ["-lc", "command -v codex"], null) is not { } output) return [];
        var path = output.Trim();
        return path.StartsWith('/') ? [path] : [];
    }

    /// <summary>Run to completion with a 5 s timeout and return stdout; <c>null</c> on failure or timeout.</summary>
    internal static string? Run(string fileName, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment)
    {
        Process process;
        try
        {
            process = Process.Start(ChildProcess.StartInfo(fileName, arguments, environment))!;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
        using (process)
        {
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(VersionTimeout))
            {
                ChildProcess.TryKillTree(process);
                return null;
            }
            return stdout.Wait(VersionTimeout) ? stdout.Result : null;
        }
    }

    private static string ExpandHome(string path)
    {
        if (path != "~" && !path.StartsWith("~/", StringComparison.Ordinal) && !path.StartsWith(@"~\", StringComparison.Ordinal))
            return path;
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        return path.Length == 1 ? home : Path.Combine(home, path[2..]);
    }
}

/// <summary>
/// Resolves npm's <c>codex.cmd</c> shim without going through <c>cmd.exe</c>, which mangles the
/// quoting of <c>-c mcp_servers={}</c> and leaves orphans (SPEC-16 §4.1). Preference: the
/// package's native <c>codex.exe</c> → <c>node &lt;entry.js&gt;</c> → <c>cmd.exe /c</c> (logged).
/// Plain file logic, so it is tested on the Mac too.
/// </summary>
internal static partial class NpmShim
{
    /// <summary>Resolve <paramref name="shimPath"/> for this machine.</summary>
    public static CodexCommand Resolve(string shimPath) =>
        Resolve(shimPath, RuntimeInformation.OSArchitecture, FindNodeOnPath);

    /// <summary>Resolve <paramref name="shimPath"/>; <paramref name="findNode"/> looks for <c>node.exe</c> on <c>PATH</c>.</summary>
    public static CodexCommand Resolve(string shimPath, Architecture architecture, Func<string?> findNode)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(shimPath)) ?? ".";
        var scope = Path.Combine(dir, "node_modules", "@openai");

        if (NativeExecutable(scope, architecture) is { } exe)
            return new CodexCommand(exe, [], shimPath, "native codex.exe from the npm package");

        var entry = EntryScript(shimPath, dir);
        if (File.Exists(entry))
        {
            var bundled = Path.Combine(dir, "node.exe");
            if ((File.Exists(bundled) ? bundled : findNode()) is { } node)
                return new CodexCommand(node, [entry], shimPath, "node + the npm package's entry script");
        }

        InterviewLog.Write($"could not resolve {shimPath} to codex.exe or node; falling back to cmd.exe /c");
        var comspec = System.Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } c ? c : "cmd.exe";
        return new CodexCommand(comspec, ["/c", shimPath], shimPath, "cmd.exe /c (last resort)");
    }

    /// <summary>
    /// A <c>codex.exe</c> for this machine's architecture inside <c>node_modules\@openai\codex\</c>
    /// (older packages vendor it directly; newer ones nest a <c>@openai/codex-win32-*</c> package)
    /// or a hoisted <c>@openai\codex-win32-*</c> sibling — recognised by the target triple or the
    /// platform package in its path. <c>null</c> when only other architectures are there, so the
    /// caller falls back to <c>node</c>, whose entry script picks the right binary itself.
    /// </summary>
    internal static string? NativeExecutable(string scope, Architecture architecture)
    {
        if (!Directory.Exists(scope)) return null;
        var roots = new List<string>();
        var package = Path.Combine(scope, "codex");
        if (Directory.Exists(package)) roots.Add(package);
        roots.AddRange(Directory.EnumerateDirectories(scope, "codex-win32-*").Order(StringComparer.Ordinal));
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 10 };
        var found = roots
            .SelectMany(r => Directory.EnumerateFiles(r, "codex.exe", options).Order(StringComparer.Ordinal))
            .ToList();
        var (triple, platformPackage) = architecture == Architecture.Arm64
            ? ("aarch64-pc-windows-msvc", "codex-win32-arm64")
            : ("x86_64-pc-windows-msvc", "codex-win32-x64");
        return found.FirstOrDefault(f => f.Contains(triple, StringComparison.OrdinalIgnoreCase)
                                         || f.Contains(platformPackage, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The <c>"%dp0%\…\x.js"</c> the shim runs, else the package's <c>bin\codex.js</c>.</summary>
    internal static string EntryScript(string shimPath, string dir)
    {
        string? relative = null;
        try
        {
            if (EntryPattern().Match(File.ReadAllText(shimPath)) is { Success: true } m) relative = m.Groups["rel"].Value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        relative ??= @"node_modules\@openai\codex\bin\codex.js";
        var parts = relative.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return Path.Combine([dir, .. parts]);
    }

    private static string? FindNodeOnPath()
    {
        var path = System.Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var d in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(d.Trim('"'), "node.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    [GeneratedRegex(@"""%~?dp0%?\\(?<rel>[^""]+?\.js)""", RegexOptions.IgnoreCase)]
    private static partial Regex EntryPattern();
}
