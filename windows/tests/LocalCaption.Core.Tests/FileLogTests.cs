using System.Diagnostics;

namespace LocalCaption.Core.Tests;

public sealed class FileLogTests
{
    [Fact]
    public void TraceGoesToTheFileAndTheLastRunIsKept()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lc-log-{Guid.NewGuid():N}");
        try
        {
            FileLog.Start(dir);
            Trace.WriteLine("first run");
            // Close the first run's file as process exit would: Windows cannot rename an open file.
            var first = Trace.Listeners["file"]!;
            Trace.Listeners.Remove(first);
            first.Dispose();
            FileLog.Start(dir);
            Trace.WriteLine("second run");
            var listener = Trace.Listeners["file"]!;
            Trace.Listeners.Remove(listener);
            listener.Dispose();

            Assert.Contains("second run", File.ReadAllText(Path.Combine(dir, "app.log")));
            Assert.Contains("first run", File.ReadAllText(Path.Combine(dir, "app.log.1")));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
