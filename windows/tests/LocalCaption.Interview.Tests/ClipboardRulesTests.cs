using System.Buffers.Binary;
using System.Text;

namespace LocalCaption.Interview.Tests;

/// <summary>
/// Clipboard screenshot rules (SPEC-16 §4.4, Swift <c>ClipboardImages</c>): source priority, size
/// limits, image files, what clearing keeps, and the DIB layouts the Windows clipboard carries.
/// </summary>
public sealed class ClipboardRulesTests
{
    private const uint CfText = 1, CfUnicodeText = 13, CfLocale = 16;

    // ── Limits ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LimitsMatchTheMacApp()
    {
        Assert.Equal(4, ClipboardRules.MaxImages);
        Assert.Equal(2048, ClipboardRules.MaxSide);
        Assert.Equal(20L * 1024 * 1024, ClipboardRules.MaxSourceBytes);
    }

    [Fact]
    public void SourcesOverTwentyMegabytesAreSkipped()
    {
        Assert.True(ClipboardRules.IsWithinSourceLimit(0));
        Assert.True(ClipboardRules.IsWithinSourceLimit(20L * 1024 * 1024));          // Swift: count <= max
        Assert.False(ClipboardRules.IsWithinSourceLimit(20L * 1024 * 1024 + 1));
        Assert.False(ClipboardRules.IsWithinSourceLimit(-1));
    }

    [Fact]
    public void ImagesClaimingAbsurdDimensionsAreRefused()
    {
        Assert.True(ClipboardRules.IsWithinPixelLimit(7680, 4320));
        Assert.True(ClipboardRules.IsWithinPixelLimit(10_000, 10_000));
        Assert.False(ClipboardRules.IsWithinPixelLimit(10_001, 10_000));
        Assert.False(ClipboardRules.IsWithinPixelLimit(0, 100));
        Assert.False(ClipboardRules.IsWithinPixelLimit(int.MaxValue, 1));
    }

    [Theory]
    [InlineData(4000, 3000, 2048, 1536)]
    [InlineData(3000, 4000, 1536, 2048)]
    [InlineData(5000, 5000, 2048, 2048)]
    [InlineData(2048, 100, 2048, 100)]       // exactly the limit: unchanged
    [InlineData(800, 600, 800, 600)]         // never enlarged
    [InlineData(4096, 2049, 2048, 1025)]     // 2049 / 2 = 1024.5: halves round up
    [InlineData(10_000, 1, 2048, 1)]         // never below one pixel
    [InlineData(1, 10_000, 1, 2048)]
    [InlineData(0, 0, 0, 0)]
    public void FitWithinKeepsAspectWithTheLongestSideAtMost2048(int w, int h, int ew, int eh) =>
        Assert.Equal((ew, eh), ClipboardRules.FitWithin(w, h));

    // ── Which source ────────────────────────────────────────────────────────────────────

    [Fact]
    public void PngBeatsDibV5BeatsDibBeatsFiles()
    {
        Assert.Equal([ClipboardImageSource.Png, ClipboardImageSource.DibV5, ClipboardImageSource.Dib, ClipboardImageSource.Files],
                     ClipboardRules.SourcePriority);
        Assert.Equal(ClipboardImageSource.Png, ClipboardRules.ChooseSource(_ => true));
        Assert.Equal(ClipboardImageSource.DibV5, ClipboardRules.ChooseSource(s => s != ClipboardImageSource.Png));
        Assert.Equal(ClipboardImageSource.Dib, ClipboardRules.ChooseSource(s => s is ClipboardImageSource.Dib or ClipboardImageSource.Files));
        Assert.Equal(ClipboardImageSource.Files, ClipboardRules.ChooseSource(s => s == ClipboardImageSource.Files));
        Assert.Null(ClipboardRules.ChooseSource(_ => false));
    }

    [Theory]
    [InlineData(@"C:\Users\me\Desktop\shot.png", true)]
    [InlineData(@"C:\a\b.JPG", true)]
    [InlineData(@"C:\a\b.jpeg", true)]
    [InlineData(@"C:\a\b.bmp", true)]
    [InlineData(@"C:\a\b.gif", true)]
    [InlineData(@"C:\a\b.tif", true)]
    [InlineData(@"C:\a\b.TIFF", true)]
    [InlineData(@"C:\a\b.heic", true)]
    [InlineData(@"C:\a\b.webp", true)]
    [InlineData(@"\\server\share\b.Png", true)]
    [InlineData(@"C:\a\notes.txt", false)]
    [InlineData(@"C:\a\shot.png.zip", false)]
    [InlineData(@"C:\folder.png\readme", false)]    // the extension is the file's, not a folder's
    [InlineData(@"C:\a\png", false)]
    [InlineData(@"C:\a\file.", false)]
    [InlineData("", false)]
    public void ImageFilesAreRecognisedByExtension(string path, bool expected) =>
        Assert.Equal(expected, ClipboardRules.IsImageFileName(path));

    [Fact]
    public void ImageFilesKeepClipboardOrder()
    {
        Assert.Equal([@"C:\b.png", @"C:\a.jpg"],
                     ClipboardRules.ImageFiles([@"C:\b.png", @"C:\doc.pdf", @"C:\a.jpg"]));
    }

    // ── Clearing ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(2u, null, ClipboardFormatKind.Image)]          // CF_BITMAP
    [InlineData(3u, null, ClipboardFormatKind.Image)]          // CF_METAFILEPICT
    [InlineData(8u, null, ClipboardFormatKind.Image)]          // CF_DIB
    [InlineData(14u, null, ClipboardFormatKind.Image)]         // CF_ENHMETAFILE
    [InlineData(17u, null, ClipboardFormatKind.Image)]         // CF_DIBV5
    [InlineData(15u, null, ClipboardFormatKind.Files)]         // CF_HDROP
    [InlineData(CfText, null, ClipboardFormatKind.Keep)]
    [InlineData(CfUnicodeText, null, ClipboardFormatKind.Keep)]
    [InlineData(CfLocale, null, ClipboardFormatKind.Keep)]
    [InlineData(0x80u, null, ClipboardFormatKind.Uncopyable)]  // CF_OWNERDISPLAY
    [InlineData(0x200u, null, ClipboardFormatKind.Uncopyable)] // CF_PRIVATEFIRST
    [InlineData(0x3FFu, null, ClipboardFormatKind.Uncopyable)] // CF_GDIOBJLAST
    [InlineData(0xC001u, "PNG", ClipboardFormatKind.Image)]
    [InlineData(0xC001u, "png", ClipboardFormatKind.Image)]
    [InlineData(0xC001u, "JFIF", ClipboardFormatKind.Image)]
    [InlineData(0xC001u, "image/svg+xml", ClipboardFormatKind.Image)]
    [InlineData(0xC001u, "HTML Format", ClipboardFormatKind.Keep)]
    [InlineData(0xC001u, "Rich Text Format", ClipboardFormatKind.Keep)]
    [InlineData(0xC001u, "Shell IDList Array", ClipboardFormatKind.FileCompanion)]
    [InlineData(0xC001u, "FileNameW", ClipboardFormatKind.FileCompanion)]
    [InlineData(0xC001u, "Preferred DropEffect", ClipboardFormatKind.Keep)]
    [InlineData(0xC001u, "DataObject", ClipboardFormatKind.Uncopyable)]
    [InlineData(0xC001u, "Ole Private Data", ClipboardFormatKind.Uncopyable)]
    public void ClearingRemovesImagesAndKeepsTheRest(uint format, string? name, ClipboardFormatKind expected) =>
        Assert.Equal(expected, ClipboardRules.Classify(format, name));

    // ── CF_HDROP ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DropFilesRoundTrip()
    {
        string[] paths = [@"C:\Users\me\Pictures\shot 1.png", @"D:\données\résumé.pdf", @"\\nas\share\x.jpg"];
        var block = ClipboardRules.BuildDropFiles(paths);
        Assert.Equal(20u, BinaryPrimitives.ReadUInt32LittleEndian(block));                 // pFiles
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(16)));         // fWide
        Assert.Equal(paths, ClipboardRules.ParseDropFiles(block));
    }

    [Fact]
    public void AnEmptyListParsesAsEmpty() =>
        Assert.Empty(ClipboardRules.ParseDropFiles(ClipboardRules.BuildDropFiles([]))!);

    [Fact]
    public void AnsiDropFilesParse()
    {
        var body = Encoding.ASCII.GetBytes("C:\\a.png\0C:\\b.txt\0\0");
        var block = new byte[20 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(block, 20);
        body.CopyTo(block, 20);
        Assert.Equal([@"C:\a.png", @"C:\b.txt"], ClipboardRules.ParseDropFiles(block));
    }

    [Fact]
    public void MalformedDropFilesAreRejectedOrTruncated()
    {
        Assert.Null(ClipboardRules.ParseDropFiles(new byte[10]));
        var badOffset = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(badOffset, 100);
        Assert.Null(ClipboardRules.ParseDropFiles(badOffset));

        // An unterminated last path is dropped; the complete one before it survives.
        var full = ClipboardRules.BuildDropFiles([@"C:\a.png", @"C:\b.png"]);
        var cut = full.AsSpan(0, full.Length - 6).ToArray();
        Assert.Equal([@"C:\a.png"], ClipboardRules.ParseDropFiles(cut));
    }

    // ── DIBs ────────────────────────────────────────────────────────────────────────────

    /// <summary>A packed DIB: header (masks inside for V2+), optional trailing masks, palette, pixels.</summary>
    private static byte[] Dib(int headerSize, int width, int height, int bitCount, uint compression, byte[] pixels,
                              uint[]? masks = null, bool masksAfterHeader = false, uint colorsUsed = 0, int paletteBytes = 0,
                              uint sizeImage = 0)
    {
        var header = new byte[headerSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)headerSize);
        if (headerSize == 12)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)width);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)height);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), (ushort)bitCount);
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), width);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), height);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), (ushort)bitCount);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), compression);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), sizeImage);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32), colorsUsed);
            if (masks is not null && headerSize >= 52)
                for (var i = 0; i < masks.Length && 40 + i * 4 + 4 <= headerSize; i++)
                    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40 + i * 4), masks[i]);
        }
        var trailing = new List<byte>();
        if (masks is not null && (headerSize == 40 || masksAfterHeader))
            foreach (var m in masksAfterHeader ? masks.Take(3) : masks)
                trailing.AddRange(BitConverter.GetBytes(m));
        return [.. header, .. trailing, .. new byte[paletteBytes], .. pixels];
    }

    private static byte[] Pixels32(params uint[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();

    private static readonly uint[] Bgra = [0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000];

    [Fact]
    public void A24BitBottomUpDib()
    {
        // 3 px × 24 bit = 9 bytes, padded to 12.
        var info = ClipboardRules.ParseDib(Dib(40, 3, 2, 24, ClipboardRules.BiRgb, new byte[24]))!.Value;
        Assert.Equal((3, 2, false, 24, 40, 12), (info.Width, info.Height, info.TopDown, info.BitCount, info.PixelOffset, info.Stride));
    }

    [Fact]
    public void ANegativeHeightIsTopDown()
    {
        var info = ClipboardRules.ParseDib(Dib(40, 2, -2, 32, ClipboardRules.BiRgb, new byte[16]))!.Value;
        Assert.True(info.TopDown);
        Assert.Equal(2, info.Height);
    }

    [Fact]
    public void BitfieldMasksFollowA40ByteHeader()
    {
        var info = ClipboardRules.ParseDib(Dib(40, 1, 1, 32, ClipboardRules.BiBitfields, new byte[4], masks: Bgra[..3]))!.Value;
        Assert.Equal(52, info.PixelOffset);
        Assert.Equal((0x00FF0000u, 0x0000FF00u, 0x000000FFu, 0u), (info.RedMask, info.GreenMask, info.BlueMask, info.AlphaMask));

        var withAlpha = ClipboardRules.ParseDib(Dib(40, 1, 1, 32, ClipboardRules.BiAlphaBitfields, new byte[4], masks: Bgra))!.Value;
        Assert.Equal(56, withAlpha.PixelOffset);
        Assert.Equal(0xFF000000u, withAlpha.AlphaMask);
    }

    [Fact]
    public void AV5HeaderHoldsItsMasks()
    {
        var info = ClipboardRules.ParseDib(Dib(124, 1, 1, 32, ClipboardRules.BiBitfields, new byte[4], masks: Bgra))!.Value;
        Assert.Equal(124, info.PixelOffset);
        Assert.Equal(0xFF000000u, info.AlphaMask);
    }

    [Fact]
    public void AV5HeaderWithMasksRepeatedAfterItIsDetected()
    {
        var pixels = Pixels32(0x80102030);
        var info = ClipboardRules.ParseDib(Dib(124, 1, 1, 32, ClipboardRules.BiBitfields, pixels, masks: Bgra, masksAfterHeader: true))!.Value;
        Assert.Equal(136, info.PixelOffset);
    }

    [Fact]
    public void PalettesSitBeforeThePixels()
    {
        Assert.Equal(40 + 256 * 4, ClipboardRules.ParseDib(Dib(40, 4, 1, 8, ClipboardRules.BiRgb, new byte[4], paletteBytes: 1024))!.Value.PixelOffset);
        Assert.Equal(40 + 2 * 4, ClipboardRules.ParseDib(Dib(40, 4, 1, 8, ClipboardRules.BiRgb, new byte[4], colorsUsed: 2, paletteBytes: 8))!.Value.PixelOffset);
        Assert.Equal(12 + 2 * 3, ClipboardRules.ParseDib(Dib(12, 8, 1, 1, ClipboardRules.BiRgb, new byte[4], paletteBytes: 6))!.Value.PixelOffset);
    }

    [Fact]
    public void AnEmbeddedPngIsLocated()
    {
        var png = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3 };
        var info = ClipboardRules.ParseDib(Dib(40, 5, 5, 0, ClipboardRules.BiPng, png, sizeImage: (uint)png.Length))!.Value;
        Assert.Equal((40, png.Length), (info.PixelOffset, info.SizeImage));
    }

    [Fact]
    public void MalformedDibsAreRejected()
    {
        Assert.Null(ClipboardRules.ParseDib(new byte[8]));
        Assert.Null(ClipboardRules.ParseDib(Dib(40, 3, 2, 24, ClipboardRules.BiRgb, new byte[23])));       // one byte short
        Assert.Null(ClipboardRules.ParseDib(Dib(40, 0, 2, 24, ClipboardRules.BiRgb, new byte[24])));       // no width
        Assert.Null(ClipboardRules.ParseDib(Dib(40, 3, 0, 24, ClipboardRules.BiRgb, new byte[24])));       // no height
        Assert.Null(ClipboardRules.ParseDib(Dib(40, 3, 2, 7, ClipboardRules.BiRgb, new byte[24])));        // no such depth
        Assert.Null(ClipboardRules.ParseDib(Dib(40, 1, 1, 0, ClipboardRules.BiPng, new byte[4], sizeImage: 99)));
        var garbage = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(garbage, 33);
        Assert.Null(ClipboardRules.ParseDib(garbage));
    }

    [Fact]
    public void A32BitRgbDibIsOpaqueWhateverItsFourthByte()
    {
        // Bottom-up 2×2: the first row in memory is the bottom one.
        var dib = Dib(40, 2, 2, 32, ClipboardRules.BiRgb, Pixels32(0x7F0000FF, 0x00000000, 0x13FF0000, 0xC000FF00));
        var px = ClipboardRules.Dib32ToBgra(dib, ClipboardRules.ParseDib(dib)!.Value)!;
        Assert.Equal(DibAlpha.Opaque, px.Alpha);
        Assert.Equal(new byte[]
        {
            0x00, 0x00, 0xFF, 0xFF,   0x00, 0xFF, 0x00, 0xFF,       // top row: red, green
            0xFF, 0x00, 0x00, 0xFF,   0x00, 0x00, 0x00, 0xFF,       // bottom row: blue, black
        }, px.Bgra);
    }

    [Fact]
    public void AV5RgbDibWithAnAlphaMaskStillDoesNotHonourIt()
    {
        // Windows' own CF_DIBV5 synthesis can carry an alpha mask with BI_RGB; the byte is padding.
        var dib = Dib(124, 1, 1, 32, ClipboardRules.BiRgb, Pixels32(0x10FF0000), masks: Bgra);
        var info = ClipboardRules.ParseDib(dib)!.Value;
        Assert.False(ClipboardRules.HonoursAlpha(info));
        Assert.Equal(DibAlpha.Opaque, ClipboardRules.Dib32ToBgra(dib, info)!.Alpha);
    }

    [Fact]
    public void StraightAlphaIsRecognised()
    {
        // 50 % opaque pure red: the red channel (255) exceeds alpha (128) — cannot be premultiplied.
        var dib = Dib(124, 1, 1, 32, ClipboardRules.BiBitfields, Pixels32(0x80FF0000), masks: Bgra);
        var px = ClipboardRules.Dib32ToBgra(dib, ClipboardRules.ParseDib(dib)!.Value)!;
        Assert.Equal(DibAlpha.Straight, px.Alpha);
        Assert.Equal(new byte[] { 0x00, 0x00, 0xFF, 0x80 }, px.Bgra);
    }

    [Fact]
    public void AlphaThatCouldBePremultipliedIsReadSo()
    {
        var dib = Dib(124, 2, 1, 32, ClipboardRules.BiBitfields, Pixels32(0x80800000, 0x00000000), masks: Bgra);
        Assert.Equal(DibAlpha.Premultiplied, ClipboardRules.Dib32ToBgra(dib, ClipboardRules.ParseDib(dib)!.Value)!.Alpha);
    }

    [Fact]
    public void DeclaredAlphaThatIsAllZeroOrAllOpaqueIsOpaque()
    {
        var zero = Dib(124, 2, 1, 32, ClipboardRules.BiBitfields, Pixels32(0x00FF0000, 0x0000FF00), masks: Bgra);
        var px = ClipboardRules.Dib32ToBgra(zero, ClipboardRules.ParseDib(zero)!.Value)!;
        Assert.Equal(DibAlpha.Opaque, px.Alpha);
        Assert.Equal(0xFF, px.Bgra[3]);
        Assert.Equal(0xFF, px.Bgra[7]);

        var full = Dib(124, 1, 1, 32, ClipboardRules.BiBitfields, Pixels32(0xFF123456), masks: Bgra);
        Assert.Equal(DibAlpha.Opaque, ClipboardRules.Dib32ToBgra(full, ClipboardRules.ParseDib(full)!.Value)!.Alpha);
    }

    [Fact]
    public void AnyChannelOrderIsReadThroughItsMasks()
    {
        // RGBA in memory order: R in the low byte.
        uint[] rgba = [0x000000FF, 0x0000FF00, 0x00FF0000, 0xFF000000];
        var dib = Dib(40, 1, 1, 32, ClipboardRules.BiAlphaBitfields, Pixels32(0xFF332211), masks: rgba);
        var px = ClipboardRules.Dib32ToBgra(dib, ClipboardRules.ParseDib(dib)!.Value)!;
        Assert.Equal(new byte[] { 0x33, 0x22, 0x11, 0xFF }, px.Bgra);     // B, G, R, A
    }

    [Fact]
    public void OnlyUncompressed32BitDibsAreConvertedHere()
    {
        var dib = Dib(40, 3, 2, 24, ClipboardRules.BiRgb, new byte[24]);
        Assert.Null(ClipboardRules.Dib32ToBgra(dib, ClipboardRules.ParseDib(dib)!.Value));
    }

    [Fact]
    public void ABmpFileHeaderPointsAtThePixels()
    {
        var dib = Dib(40, 4, 1, 8, ClipboardRules.BiRgb, new byte[4], colorsUsed: 2, paletteBytes: 8);
        var file = ClipboardRules.DibToBmpFile(dib, ClipboardRules.ParseDib(dib)!.Value);
        Assert.Equal((byte)'B', file[0]);
        Assert.Equal((byte)'M', file[1]);
        Assert.Equal((uint)file.Length, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(2)));
        Assert.Equal(14u + 48u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(10)));
        Assert.Equal(dib, file[14..]);
    }
}
