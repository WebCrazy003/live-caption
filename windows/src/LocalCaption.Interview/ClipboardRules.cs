using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace LocalCaption.Interview;

/// <summary>Where a clipboard image is read from, in <see cref="ClipboardRules.SourcePriority"/> order.</summary>
public enum ClipboardImageSource
{
    /// <summary>The registered <c>"PNG"</c> format (browsers, Office, Snipping Tool).</summary>
    Png,
    /// <summary><c>CF_DIBV5</c> — the only DIB that can carry alpha.</summary>
    DibV5,
    /// <summary><c>CF_DIB</c>.</summary>
    Dib,
    /// <summary><c>CF_HDROP</c>: files copied in Explorer, kept when their extension is an image's.</summary>
    Files,
}

/// <summary>What clearing the clipboard after a send does with one format (<see cref="ClipboardRules.Classify"/>).</summary>
public enum ClipboardFormatKind
{
    /// <summary>Not an image: written back byte for byte, never inspected.</summary>
    Keep,
    /// <summary>An image rendering: removed.</summary>
    Image,
    /// <summary><c>CF_HDROP</c>: the image files are taken out of the list; the rest stay.</summary>
    Files,
    /// <summary>Describes the same files as <c>CF_HDROP</c> (shell ID lists, file names): removed when that list changes.</summary>
    FileCompanion,
    /// <summary>A handle that is not an <c>HGLOBAL</c> (GDI objects, private formats, OLE's own): cannot be copied, so lost.</summary>
    Uncopyable,
}

/// <summary>How a 32-bit DIB's fourth byte is to be read.</summary>
public enum DibAlpha
{
    /// <summary>No alpha: the byte is padding (or every pixel's is 0, which means the same).</summary>
    Opaque,
    /// <summary>Straight alpha: some colour channel exceeds its alpha, so it cannot be premultiplied.</summary>
    Straight,
    /// <summary>Alpha that may be premultiplied (every channel ≤ alpha) — read as premultiplied.</summary>
    Premultiplied,
}

/// <summary>A parsed <c>BITMAPINFOHEADER</c>-family header (<see cref="ClipboardRules.ParseDib"/>).</summary>
/// <param name="HeaderSize">12 (<c>BITMAPCOREHEADER</c>), 40, 52, 56, 108 (V4) or 124 (V5).</param>
/// <param name="Height">Always positive; <paramref name="TopDown"/> says which way the rows run.</param>
/// <param name="PixelOffset">Where the pixels start, from the start of the DIB (past masks and colour table).</param>
/// <param name="Stride">Bytes per row (rows are padded to 4 bytes) for uncompressed DIBs.</param>
/// <param name="SizeImage"><c>biSizeImage</c>: the embedded JPEG/PNG's length for <c>BI_JPEG</c>/<c>BI_PNG</c>.</param>
public readonly record struct DibInfo(
    int HeaderSize, int Width, int Height, bool TopDown, int BitCount, uint Compression,
    uint RedMask, uint GreenMask, uint BlueMask, uint AlphaMask,
    int PixelOffset, int Stride, int SizeImage);

/// <summary>A 32-bit DIB as top-down BGRA rows, <c>Width * 4</c> bytes each.</summary>
public sealed record DibPixels(int Width, int Height, byte[] Bgra, DibAlpha Alpha)
{
    public int Stride => Width * 4;
}

/// <summary>
/// The rules of clipboard screenshots on Windows (SPEC-16 §4.4, port of <c>ClipboardImages.swift</c>):
/// which format to read, the size limits, which files count as images, what clearing keeps, and
/// the DIB layouts. Pure — bytes in, bytes out, no Win32 or WPF — so it is tested on the Mac; the
/// app's <c>ClipboardImages</c> does the clipboard calls and the decoding.
/// </summary>
public static class ClipboardRules
{
    /// <summary>Images taken per read (Swift <c>maxImages</c>).</summary>
    public const int MaxImages = 4;

    /// <summary>The longest side of a re-encoded image, pixels (Swift <c>maxSide</c>).</summary>
    public const int MaxSide = 2048;

    /// <summary>Sources bigger than this are skipped (Swift <c>maxSourceBytes</c>).</summary>
    public const long MaxSourceBytes = 20L * 1024 * 1024;

    /// <summary>
    /// Decoded size above which an image is skipped: a 20 MB PNG can claim a gigapixel. 100 MP is
    /// beyond any screenshot (three 8K monitors are 100 MP).
    /// </summary>
    public const long MaxSourcePixels = 100_000_000;

    /// <summary>A <c>CF_HDROP</c> list longer than this is not parsed (thousands of paths, none of them screenshots).</summary>
    public const int MaxFileListBytes = 4 * 1024 * 1024;

    /// <summary>Clearing never empties a clipboard whose other formats add up to more than this: what cannot be put back is not taken away.</summary>
    public const long MaxPreservedBytes = 256L * 1024 * 1024;

    /// <summary>The registered clipboard format name for PNG bytes.</summary>
    public const string PngFormatName = "PNG";

    // Standard clipboard formats (WinUser.h).
    public const uint CfBitmap = 2, CfMetafilePict = 3, CfTiff = 6, CfDib = 8, CfPalette = 9,
                      CfEnhMetafile = 14, CfHdrop = 15, CfDibV5 = 17,
                      CfOwnerDisplay = 0x0080, CfDspBitmap = 0x0082, CfDspMetafilePict = 0x0083, CfDspEnhMetafile = 0x008E,
                      CfPrivateFirst = 0x0200, CfGdiObjLast = 0x03FF;

    // biCompression values (wingdi.h).
    public const uint BiRgb = 0, BiRle8 = 1, BiRle4 = 2, BiBitfields = 3, BiJpeg = 4, BiPng = 5, BiAlphaBitfields = 6;

    /// <summary>PNG, then the DIBs, then image files (SPEC-16 §4.4).</summary>
    public static IReadOnlyList<ClipboardImageSource> SourcePriority { get; } =
        [ClipboardImageSource.Png, ClipboardImageSource.DibV5, ClipboardImageSource.Dib, ClipboardImageSource.Files];

    /// <summary>The first source in <see cref="SourcePriority"/> that <paramref name="isAvailable"/> says is there; null when none is.</summary>
    public static ClipboardImageSource? ChooseSource(Func<ClipboardImageSource, bool> isAvailable)
    {
        foreach (var s in SourcePriority)
            if (isAvailable(s)) return s;
        return null;
    }

    /// <summary>Whether a source of <paramref name="bytes"/> is read at all (≤ 20 MB, like Swift's <c>count &lt;= maxSourceBytes</c>).</summary>
    public static bool IsWithinSourceLimit(long bytes) => bytes >= 0 && bytes <= MaxSourceBytes;

    /// <summary>Whether an image this size is decoded at all (see <see cref="MaxSourcePixels"/>).</summary>
    public static bool IsWithinPixelLimit(long width, long height) =>
        width > 0 && height > 0 && width <= int.MaxValue / 4 && height <= int.MaxValue / 4 && width * height <= MaxSourcePixels;

    /// <summary>
    /// The size an image is re-encoded at: unchanged when the longest side is ≤ <paramref name="maxSide"/>,
    /// else scaled to exactly <paramref name="maxSide"/> on that side, the other rounded, never below 1.
    /// </summary>
    public static (int Width, int Height) FitWithin(int width, int height, int maxSide = MaxSide)
    {
        if (width <= 0 || height <= 0 || Math.Max(width, height) <= maxSide) return (width, height);
        return width >= height
            ? (maxSide, Math.Max(1, (int)Math.Round((double)height * maxSide / width, MidpointRounding.AwayFromZero)))
            : (Math.Max(1, (int)Math.Round((double)width * maxSide / height, MidpointRounding.AwayFromZero)), maxSide);
    }

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "bmp", "gif", "tif", "tiff", "heic", "heif", "webp",
    };

    /// <summary>
    /// Whether a copied file counts as an image, by extension. HEIC/HEIF and WebP are listed, but
    /// decode only where the Windows codec is installed; a file that does not decode is skipped.
    /// </summary>
    public static bool IsImageFileName(string path)
    {
        // Not Path.GetExtension: these are Windows paths, and the tests run where '\' is no separator.
        var name = path[(path.LastIndexOfAny(['\\', '/']) + 1)..];
        var dot = name.LastIndexOf('.');
        return dot >= 0 && dot < name.Length - 1 && ImageExtensions.Contains(name[(dot + 1)..]);
    }

    /// <summary>The image files of a copied file list, in clipboard order.</summary>
    public static IReadOnlyList<string> ImageFiles(IEnumerable<string> paths) => paths.Where(IsImageFileName).ToList();

    private static readonly HashSet<uint> StandardImageFormats =
        [CfBitmap, CfMetafilePict, CfTiff, CfDib, CfPalette, CfEnhMetafile, CfDibV5, CfDspBitmap, CfDspMetafilePict, CfDspEnhMetafile];

    private static readonly HashSet<string> ImageFormatNames = new(StringComparer.OrdinalIgnoreCase)
    {
        PngFormatName, "JFIF", "GIF", "TIFF", "BMP",
    };

    private static readonly HashSet<string> FileCompanionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell IDList Array", "FileName", "FileNameW", "FileGroupDescriptor", "FileGroupDescriptorW",
        "FileContents", "Shell Object Offsets",
    };

    /// <summary>
    /// OLE's bookkeeping: <c>DataObject</c> holds a pointer into the process that copied, and
    /// <c>Ole Private Data</c> describes that object. Written back under another owner they would
    /// send OLE paste (WPF, WinForms, Office) after a stale object; without them OLE wraps the plain
    /// formats instead, which works.
    /// </summary>
    private static readonly HashSet<string> UncopyableNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "DataObject", "Ole Private Data",
    };

    /// <summary>Whether a registered format's name is an image's (<c>PNG</c>, <c>JFIF</c>, <c>image/…</c>).</summary>
    public static bool IsImageFormatName(string name) =>
        ImageFormatNames.Contains(name) || name.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What clearing does with a format (Swift <c>removeImages</c>: images go, everything else is
    /// put back verbatim). <paramref name="registeredName"/> is <c>GetClipboardFormatName</c>'s
    /// answer for registered formats (≥ 0xC000), null for standard ones.
    /// </summary>
    public static ClipboardFormatKind Classify(uint format, string? registeredName)
    {
        if (format == CfHdrop) return ClipboardFormatKind.Files;
        if (StandardImageFormats.Contains(format)) return ClipboardFormatKind.Image;
        // CF_OWNERDISPLAY has no data; CF_PRIVATEFIRST..CF_GDIOBJLAST are private handles and GDI objects.
        if (format == CfOwnerDisplay || format is >= CfPrivateFirst and <= CfGdiObjLast) return ClipboardFormatKind.Uncopyable;
        if (registeredName is null) return ClipboardFormatKind.Keep;
        if (IsImageFormatName(registeredName)) return ClipboardFormatKind.Image;
        if (UncopyableNames.Contains(registeredName)) return ClipboardFormatKind.Uncopyable;
        if (FileCompanionNames.Contains(registeredName)) return ClipboardFormatKind.FileCompanion;
        return ClipboardFormatKind.Keep;
    }

    // ── CF_HDROP ─────────────────────────────────────────────────────────────────────────

    private const int DropFilesSize = 20;   // DROPFILES { DWORD pFiles; POINT pt; BOOL fNC; BOOL fWide; }

    /// <summary>
    /// The paths in a <c>CF_HDROP</c> block (a <c>DROPFILES</c> header, then null-terminated
    /// strings ending with an empty one). Null when the header is malformed; an unterminated last
    /// path is dropped.
    /// </summary>
    public static IReadOnlyList<string>? ParseDropFiles(ReadOnlySpan<byte> data)
    {
        if (data.Length < DropFilesSize) return null;
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var wide = BinaryPrimitives.ReadInt32LittleEndian(data[16..]) != 0;
        if (offset < DropFilesSize || offset > data.Length) return null;

        var paths = new List<string>();
        var i = (int)offset;
        var unit = wide ? 2 : 1;
        while (i + unit <= data.Length)
        {
            var start = i;
            while (i + unit <= data.Length && (data[i] != 0 || (wide && data[i + 1] != 0))) i += unit;
            if (i + unit > data.Length) break;      // unterminated
            if (i == start) break;                  // the empty string that ends the list
            // ANSI lists come only from very old programs; Latin-1 keeps ASCII paths right, and a
            // mangled non-ASCII one is simply a file that is not found.
            paths.Add(wide ? Encoding.Unicode.GetString(data[start..i]) : Encoding.Latin1.GetString(data[start..i]));
            i += unit;
        }
        return paths;
    }

    /// <summary>A wide-character <c>CF_HDROP</c> block listing <paramref name="paths"/>.</summary>
    public static byte[] BuildDropFiles(IReadOnlyList<string> paths)
    {
        var body = new List<byte>();
        foreach (var p in paths)
        {
            body.AddRange(Encoding.Unicode.GetBytes(p));
            body.Add(0);
            body.Add(0);
        }
        body.Add(0);
        body.Add(0);
        var block = new byte[DropFilesSize + body.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(block, DropFilesSize);         // pFiles
        BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(16), 1);           // fWide
        body.CopyTo(block, DropFilesSize);
        return block;
    }

    // ── DIBs ─────────────────────────────────────────────────────────────────────────────

    private const uint ProfileEmbedded = 0x4D424544;    // 'MBED'

    /// <summary>
    /// Read a packed DIB's header (<c>CF_DIB</c>/<c>CF_DIBV5</c> data: header, masks, colour
    /// table, pixels). Null when it is malformed or the pixels would run past the data.
    /// </summary>
    /// <remarks>
    /// <para><b>Masks.</b> With <c>BI_BITFIELDS</c> a 40-byte header is followed by three DWORD
    /// masks (four with <c>BI_ALPHABITFIELDS</c>); V2+ headers hold them inside. Either way they are
    /// at bytes 40–55.</para>
    /// <para><b>The CF_DIBV5 quirk.</b> Some producers put the three masks after a V5 header as
    /// well, as if it were a 40-byte one. That is detected by the bytes at the pixel offset
    /// repeating the header's masks with room to spare, and skipped.</para>
    /// <para><b>Colour table.</b> <c>biClrUsed</c> entries when set — also for 16/24/32-bit DIBs,
    /// where the table is an optional palette hint that still sits before the pixels — else
    /// 2^bits for ≤ 8-bit. Three-byte entries after a <c>BITMAPCOREHEADER</c>.</para>
    /// </remarks>
    public static DibInfo? ParseDib(ReadOnlySpan<byte> dib)
    {
        if (dib.Length < 12) return null;
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(dib);
        int width, height, bitCount, entrySize;
        uint compression = BiRgb, sizeImage = 0, colorsUsed = 0;
        if (headerSize == 12)
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(dib[4..]);
            height = BinaryPrimitives.ReadUInt16LittleEndian(dib[6..]);     // unsigned: always bottom-up
            bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib[10..]);
            entrySize = 3;
        }
        else if (headerSize >= 40 && headerSize <= 4096 && headerSize <= dib.Length)
        {
            width = BinaryPrimitives.ReadInt32LittleEndian(dib[4..]);
            height = BinaryPrimitives.ReadInt32LittleEndian(dib[8..]);
            bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
            compression = BinaryPrimitives.ReadUInt32LittleEndian(dib[16..]);
            sizeImage = BinaryPrimitives.ReadUInt32LittleEndian(dib[20..]);
            colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]);
            entrySize = 4;
        }
        else return null;

        if (width <= 0 || height == 0 || height == int.MinValue) return null;
        var embedded = compression is BiJpeg or BiPng;
        if (!embedded && bitCount is not (1 or 4 or 8 or 16 or 24 or 32)) return null;
        if (compression > BiAlphaBitfields) return null;
        var topDown = height < 0;
        height = Math.Abs(height);

        uint r = 0, g = 0, b = 0, a = 0;
        var maskBytes = 0;
        if (compression is BiBitfields or BiAlphaBitfields)
        {
            if (headerSize == 40) maskBytes = compression == BiAlphaBitfields ? 16 : 12;
            else if (headerSize < 52) return null;
            if (40 + 12 > dib.Length) return null;
            r = BinaryPrimitives.ReadUInt32LittleEndian(dib[40..]);
            g = BinaryPrimitives.ReadUInt32LittleEndian(dib[44..]);
            b = BinaryPrimitives.ReadUInt32LittleEndian(dib[48..]);
            if ((compression == BiAlphaBitfields || headerSize >= 56) && 56 <= dib.Length)
                a = BinaryPrimitives.ReadUInt32LittleEndian(dib[52..]);
        }
        else if (compression == BiRgb)
        {
            (r, g, b) = bitCount == 16 ? (0x7C00u, 0x03E0u, 0x001Fu) : (0x00FF0000u, 0x0000FF00u, 0x000000FFu);
            if (headerSize >= 56) a = BinaryPrimitives.ReadUInt32LittleEndian(dib[52..]);   // noted, not honoured: see HonoursAlpha
        }

        long colors = colorsUsed != 0 ? colorsUsed : bitCount is >= 1 and <= 8 ? 1L << bitCount : 0;
        if (headerSize == 12) colors = bitCount <= 8 ? 1L << bitCount : 0;
        if (colors > 65536) return null;

        long pixelOffset = headerSize + maskBytes + colors * entrySize;
        long stride = ((long)width * bitCount + 31) / 32 * 4;
        var uncompressed = compression is BiRgb or BiBitfields or BiAlphaBitfields;
        long pixelBytes = uncompressed ? stride * height : sizeImage;
        if (pixelOffset > dib.Length || stride > int.MaxValue) return null;

        if (headerSize >= 108 && compression == BiBitfields && pixelOffset + 12 + pixelBytes <= dib.Length
            && BinaryPrimitives.ReadUInt32LittleEndian(dib[(int)pixelOffset..]) == r
            && BinaryPrimitives.ReadUInt32LittleEndian(dib[((int)pixelOffset + 4)..]) == g
            && BinaryPrimitives.ReadUInt32LittleEndian(dib[((int)pixelOffset + 8)..]) == b
            && BinaryPrimitives.ReadUInt32LittleEndian(dib[56..]) != ProfileEmbedded)
            pixelOffset += 12;

        if (uncompressed && pixelOffset + pixelBytes > dib.Length) return null;
        if (embedded && (sizeImage == 0 || pixelOffset + sizeImage > dib.Length)) return null;

        return new DibInfo((int)headerSize, width, height, topDown, bitCount, compression, r, g, b, a,
                           (int)pixelOffset, (int)stride, (int)Math.Min(sizeImage, int.MaxValue));
    }

    /// <summary>
    /// Whether a DIB's fourth byte is alpha. Only when the producer says so: a 32-bit
    /// <c>BI_BITFIELDS</c>/<c>BI_ALPHABITFIELDS</c> DIB with an alpha mask (what Firefox, GIMP
    /// and the like write to <c>CF_DIBV5</c>). A <c>BI_RGB</c> DIB's fourth byte is padding by
    /// definition and is often garbage — Windows synthesises <c>CF_DIBV5</c> from other formats,
    /// so believing it would turn ordinary screenshots transparent.
    /// </summary>
    public static bool HonoursAlpha(DibInfo info) =>
        info.BitCount == 32 && info.Compression is BiBitfields or BiAlphaBitfields && info.AlphaMask != 0;

    /// <summary>
    /// A 32-bit uncompressed DIB as top-down BGRA (any channel masks), with how to read its
    /// alpha (<see cref="DibAlpha"/>). Null for any other DIB — those go through
    /// <see cref="DibToBmpFile"/> and the system BMP decoder.
    /// </summary>
    public static DibPixels? Dib32ToBgra(ReadOnlySpan<byte> dib, DibInfo info)
    {
        if (info.BitCount != 32 || info.Compression is not (BiRgb or BiBitfields or BiAlphaBitfields)) return null;
        long total = (long)info.Width * info.Height * 4;
        if (total > int.MaxValue) return null;

        var honour = HonoursAlpha(info);
        var red = new Channel(info.RedMask);
        var green = new Channel(info.GreenMask);
        var blue = new Channel(info.BlueMask);
        var alpha = new Channel(honour ? info.AlphaMask : 0);

        var output = new byte[total];
        bool anyAlpha = false, allOpaque = true, colourAboveAlpha = false;
        var o = 0;
        for (var y = 0; y < info.Height; y++)
        {
            var row = info.PixelOffset + (long)(info.TopDown ? y : info.Height - 1 - y) * info.Stride;
            var line = dib.Slice((int)row, info.Width * 4);
            for (var x = 0; x < info.Width; x++)
            {
                var v = BinaryPrimitives.ReadUInt32LittleEndian(line[(x * 4)..]);
                byte rv = red.Read(v), gv = green.Read(v), bv = blue.Read(v);
                var av = honour ? alpha.Read(v) : (byte)255;
                if (honour)
                {
                    if (av != 0) anyAlpha = true;
                    if (av != 255) allOpaque = false;
                    if (rv > av || gv > av || bv > av) colourAboveAlpha = true;
                }
                output[o] = bv;
                output[o + 1] = gv;
                output[o + 2] = rv;
                output[o + 3] = av;
                o += 4;
            }
        }

        DibAlpha kind;
        if (!honour || !anyAlpha || allOpaque)
        {
            kind = DibAlpha.Opaque;
            // All-zero alpha is a producer that declared alpha and never wrote it: opaque.
            if (honour) for (var i = 3; i < output.Length; i += 4) output[i] = 255;
        }
        else kind = colourAboveAlpha ? DibAlpha.Straight : DibAlpha.Premultiplied;
        return new DibPixels(info.Width, info.Height, output, kind);
    }

    /// <summary>
    /// The DIB as a <c>.bmp</c> file: a <c>BITMAPFILEHEADER</c> whose <c>bfOffBits</c> points
    /// where <see cref="ParseDib"/> found the pixels, then the DIB unchanged — what the system BMP
    /// decoder needs for palettes, 16/24-bit, RLE and the rest.
    /// </summary>
    public static byte[] DibToBmpFile(ReadOnlySpan<byte> dib, DibInfo info)
    {
        var file = new byte[14 + dib.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(2), (uint)file.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(10), (uint)(14 + info.PixelOffset));
        dib.CopyTo(file.AsSpan(14));
        return file;
    }

    /// <summary>One colour mask: where its bits are, and how to stretch them to 8 bits.</summary>
    private readonly struct Channel
    {
        private readonly uint _mask;
        private readonly int _shift;
        private readonly int _bits;

        public Channel(uint mask)
        {
            _mask = mask;
            _shift = mask == 0 ? 0 : BitOperations.TrailingZeroCount(mask);
            _bits = BitOperations.PopCount(mask);
        }

        public byte Read(uint pixel)
        {
            if (_bits == 0) return 0;
            var v = (pixel & _mask) >> _shift;
            return _bits switch
            {
                8 => (byte)v,
                > 8 => (byte)(v >> (_bits - 8)),
                _ => (byte)(v * 255 / ((1u << _bits) - 1)),
            };
        }
    }
}
