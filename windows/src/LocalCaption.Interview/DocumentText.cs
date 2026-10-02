using System.Text;
using LocalCaption.Core.Data;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace LocalCaption.Interview;

/// <summary>
/// Text extraction for imported documents (SPEC-13 §Documents, SPEC-16 §4.5). What this returns
/// is what gets sent, so it is shown to the user and editable. A port of macOS
/// <c>DocumentText</c>, with PdfPig in place of PDFKit.
/// </summary>
public static class DocumentText
{
    /// <summary>Why a file could not be turned into text, worded for the user.</summary>
    public sealed class Failure(string message) : Exception(message)
    {
        public static Failure ScannedPdf() => new("This PDF is a scanned image; paste the text instead.");
        public static Failure Unreadable() => new("Couldn't read that file.");
        public static Failure Unsupported(string ext) =>
            new($"“.{ext}” files aren't supported — use PDF, Markdown or plain text, or paste the text.");
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    static DocumentText() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <exception cref="Failure">Unsupported type, unreadable file, or a PDF with no text.</exception>
    public static string Extract(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        switch (ext)
        {
            case "pdf":
                List<string> pages;
                try
                {
                    using var pdf = PdfDocument.Open(path);
                    pages = pdf.GetPages()
                        .Select(p => ContentOrderTextExtractor.GetText(p).Trim())
                        .Where(t => t.Length > 0)
                        .ToList();
                }
                catch (Exception e) when (e is not Failure) { throw Failure.Unreadable(); }
                if (pages.Count == 0) throw Failure.ScannedPdf();
                return Normalize(string.Join("\n\n", pages));
            case "md" or "markdown" or "txt" or "text":
                return Normalize(ReadText(path));
            default:
                throw Failure.Unsupported(ext);
        }
    }

    /// <summary>UTF-8 (a byte-order mark dropped, as Windows editors add one), falling back to Windows-1252.</summary>
    public static string ReadText(string path)
    {
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw Failure.Unreadable(); }
        var bom = data.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? 3 : 0;
        try { return StrictUtf8.GetString(data, bom, data.Length - bom); }
        catch (DecoderFallbackException) { return Encoding.GetEncoding(1252).GetString(data); }
    }

    /// <summary><c>\r\n</c> / <c>\r</c> → <c>\n</c>, surrounding whitespace trimmed.</summary>
    public static string Normalize(string s) => Files.Lf(s).Trim();
}
