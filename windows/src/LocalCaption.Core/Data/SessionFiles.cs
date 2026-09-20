namespace LocalCaption.Core.Data;

/// <summary>Filesystem operations on a session's saved transcript artifacts.</summary>
public static class SessionFiles
{
    /// <summary>
    /// Delete a transcript <c>.txt</c> and its sibling <c>.json</c> sidecar together
    /// (SPEC-06 delete). Returns the paths actually removed.
    /// </summary>
    public static IReadOnlyList<string> DeleteTranscript(string txtPath)
    {
        var jsonPath = Path.ChangeExtension(txtPath, ".json");
        var removed = new List<string>();
        foreach (var path in new[] { txtPath, jsonPath })
        {
            if (!File.Exists(path)) continue;
            try { File.Delete(path); removed.Add(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return removed;
    }
}
