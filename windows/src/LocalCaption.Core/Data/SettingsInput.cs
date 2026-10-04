namespace LocalCaption.Core.Data;

/// <summary>
/// The limits and checks behind Settings' typed-in values (specs/SPEC-16 §6, C6 and C7), kept
/// out of the WPF window so they are tested on every platform.
/// </summary>
/// <remarks>
/// The ranges are the Mac's Steppers (<c>LC/UI/SettingsView.swift</c>): end-of-speech silence
/// 200–2000 ms in steps of 50, longest phrase 5–60 s. A value is clamped, never snapped to the
/// step — the Mac's Stepper steps from whatever the value is, so a Mac config holding 625 ms
/// stays 625 ms when it is saved from Windows.
/// </remarks>
public static class SettingsInput
{
    public const int EndpointSilenceMinMs = 200;
    public const int EndpointSilenceMaxMs = 2000;
    public const int EndpointSilenceStepMs = 50;
    public const int MaxUtteranceMinS = 5;
    public const int MaxUtteranceMaxS = 60;
    public const int MaxUtteranceStepS = 1;

    /// <summary>End-of-speech silence, clamped to 200–2000 ms.</summary>
    public static int EndpointSilenceMs(int value) => Math.Clamp(value, EndpointSilenceMinMs, EndpointSilenceMaxMs);

    /// <summary>Longest phrase, clamped to 5–60 s.</summary>
    public static int MaxUtteranceS(int value) => Math.Clamp(value, MaxUtteranceMinS, MaxUtteranceMaxS);

    /// <summary>The Mac's words for a folder that cannot be written.</summary>
    public const string UnwritableFolder = "That folder isn't writable — pick another.";

    /// <summary>
    /// Why transcripts cannot be saved in <paramref name="folder"/>, or null when they can.
    /// </summary>
    /// <remarks>
    /// <para>Proved by doing it: the folder is created if it is missing, then a uniquely named
    /// empty file is created in it and deleted again. Permission bits and ACLs say what should
    /// work; a read-only network share, a full or ejected drive, or controlled folder access
    /// only show up when a file is actually written.</para>
    /// <para>A relative path is refused: the transcript writer would resolve it against
    /// whatever the working directory happens to be.</para>
    /// </remarks>
    public static string? FolderProblem(string? folder)
    {
        var path = folder?.Trim();
        if (string.IsNullOrEmpty(path)) return "Choose a folder for transcripts.";

        try
        {
            if (!Path.IsPathFullyQualified(path)) return "Use a full path to a folder, such as D:\\Transcripts.";
        }
        catch (ArgumentException)
        {
            return "That is not a valid folder path.";
        }

        var probe = "";
        try
        {
            Directory.CreateDirectory(path);
            probe = Path.Combine(path, $".localcaption-write-test-{Guid.NewGuid():N}.tmp");
            using (File.Create(probe)) { }
            File.Delete(probe);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException
                                       or ArgumentException or System.Security.SecurityException)
        {
            // A probe created but not deleted is tidied if it can be; a folder that let it be
            // written is still not one that can be trusted with transcripts.
            try { if (probe.Length > 0 && File.Exists(probe)) File.Delete(probe); }
            catch (Exception) { }
            return UnwritableFolder;
        }
    }
}
