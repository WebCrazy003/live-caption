namespace LocalCaption.Interview;

/// <summary>
/// Lets the app's platform services (hotkeys, screenshot overlay, clipboard images) write to
/// <see cref="InterviewLog"/>, whose writer is internal to this assembly — so their lines reach
/// the same <see cref="InterviewLog.Message"/> subscribers and <c>Trace</c> as the controller's.
/// </summary>
public static class InterviewPlatformLog
{
    /// <summary>Log <paramref name="line"/> under <paramref name="area"/> (e.g. <c>"screenshot"</c>). Never throws.</summary>
    public static void Write(string area, string line)
    {
        try
        {
            InterviewLog.Write($"{area}: {line}");
        }
        catch (Exception)
        {
            // Logging must never be what breaks a capture.
        }
    }
}
