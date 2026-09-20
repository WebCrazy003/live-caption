using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace LocalCaption.App;

/// <summary>
/// Puts text on the clipboard without ever letting the clipboard end a recording.
/// </summary>
/// <remarks>
/// <para><b>SPEC-WINDOWS.md §7.4.</b> <c>Clipboard.SetText</c> throws when another process
/// holds the clipboard open, which is not rare: Windows 11 Clipboard History is on by
/// default and watches every change, as do Office and every clipboard manager — and under
/// Jump Desktop's clipboard sync it is routine (§4.7.4). The spec calls an unhandled throw
/// here "a one-line bug with a big blast radius", because it would kill a session
/// mid-interview.</para>
/// <para>Ten attempts over roughly 500 ms, then give up silently and leave the "Copied"
/// indicator off. The transcript file is the durable artefact; the clipboard is a
/// convenience.</para>
/// <para>Write-only, always. The app never reads the clipboard (§12.1).</para>
/// </remarks>
public static class ClipboardWriter
{
    private const int Attempts = 10;
    private const int BackoffMs = 50;

    /// <summary>
    /// Copy <paramref name="text"/>, returning whether it landed. Safe to call from any
    /// thread: the clipboard requires STA, so the work is marshalled to the UI thread.
    /// </summary>
    public static bool Copy(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return false;

        return dispatcher.CheckAccess()
            ? TrySet(text)
            : dispatcher.Invoke(() => TrySet(text), DispatcherPriority.Normal);
    }

    private static bool TrySet(string text)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (COMException)
            {
                // Someone else has it open. They will let go.
            }
            catch (ExternalException)
            {
            }

            Thread.Sleep(BackoffMs);
        }

        return false;
    }
}
