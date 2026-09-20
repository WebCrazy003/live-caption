namespace LocalCaption.Asr;

/// <summary>
/// Fetches speech-model weights once into the models directory.
/// </summary>
/// <remarks>
/// <para>This is the <b>only</b> network access in the product's lifetime (SPEC.md §1.2 /
/// SPEC-WINDOWS.md §1.2). No telemetry, no update checks, no audio or transcript ever
/// leaves the device. The privacy invariant is an acceptance criterion verified with a
/// network monitor (§17.13), so nothing else may acquire an <see cref="HttpClient"/>.</para>
/// <para>Downloads land on a temp file and are renamed into place only once complete, so an
/// interrupted download can never be mistaken for a usable model on the next launch —
/// which would otherwise surface as a baffling load failure rather than a retry.</para>
/// </remarks>
public sealed class ModelDownloader(HttpClient? client = null) : IDisposable
{
    private readonly HttpClient _client = client ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    private readonly bool _ownsClient = client is null;

    /// <summary>Fraction complete, 0–1. Indeterminate when the server sends no length.</summary>
    public delegate void Progress(string modelName, double fraction);

    /// <summary>
    /// Ensure the model is on disk, downloading only if missing. Returns its path.
    /// </summary>
    public async Task<string> EnsureAsync(ModelSpec spec, string modelsDirectory,
                                          Progress? onProgress = null,
                                          CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(modelsDirectory);
        var destination = ModelCatalog.PathFor(spec, modelsDirectory);
        if (File.Exists(destination) && new FileInfo(destination).Length > 0) return destination;

        var temp = destination + ".part";
        try
        {
            using var response = await _client
                .GetAsync(spec.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? spec.ApproxBytes;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None,
                                                   bufferSize: 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long written = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    written += read;
                    if (total > 0) onProgress?.Invoke(spec.Name, Math.Min(1.0, (double)written / total));
                }
            }

            File.Move(temp, destination, overwrite: true);
            return destination;
        }
        catch
        {
            // Never leave a partial file where a complete one belongs.
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
            throw;
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }
}
