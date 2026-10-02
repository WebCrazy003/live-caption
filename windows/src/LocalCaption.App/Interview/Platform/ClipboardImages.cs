using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LocalCaption.Interview;

namespace LocalCaption.App.Interview.Platform;

/// <summary>
/// The clipboard as the screenshot tray needs it (SPEC-16 §4.4; port of
/// <c>ClipboardImages.swift</c>). The only place the app reads the clipboard, and only images:
/// text formats are never requested on the read path. Clearing after a send puts every non-image
/// format back byte for byte — never inspected, logged or sent.
/// </summary>
/// <remarks>
/// <para><b>Contention.</b> Another process can hold the clipboard open (Windows Clipboard
/// History, Office, clipboard managers, remote-desktop sync — SPEC-WINDOWS §12.1).
/// <c>OpenClipboard</c> is retried briefly and the clipboard is held only while bytes are copied
/// out; decoding happens after <c>CloseClipboard</c>. If it stays busy, <see cref="ReadImages"/>
/// throws <see cref="ClipboardBusyException"/> — the controller then keeps the old sequence number
/// and tries again on the next poll (an empty result would mark the change as seen and lose the
/// image). Every other member, and every other failure, never throws.</para>
/// <para><b>Threads.</b> Create on the UI thread. <see cref="SequenceNumber"/>,
/// <see cref="ContainsImages"/> and <see cref="ReadImages"/> work from any thread (the decoding
/// objects belong to the caller's). <see cref="ClearIfUnchanged"/> runs on the UI thread, because
/// writing needs an owner window whose thread pumps messages — Windows sends
/// <c>WM_DESTROYCLIPBOARD</c> to it, and an owner that never answers would hang whoever empties
/// the clipboard next.</para>
/// </remarks>
public sealed class ClipboardImages : IClipboardImages, IDisposable
{
    private const int ReadAttempts = 8;
    private const int ClearAttempts = 10;
    private const int RetryDelayMs = 25;

    private readonly Dispatcher _dispatcher;
    private readonly uint _pngFormat;
    private HwndSource? _owner;
    private bool _disposed;

    /// <summary>Create on the UI thread.</summary>
    public ClipboardImages()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        try { _pngFormat = RegisterClipboardFormat(ClipboardRules.PngFormatName); }
        catch (Exception e) { Log($"registering the PNG format failed: {e.Message}"); }
    }

    /// <inheritdoc />
    public long SequenceNumber
    {
        get
        {
            try { return GetClipboardSequenceNumber(); }
            catch (Exception) { return 0; }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// PNG or a DIB (<c>CF_BITMAP</c> counts: Windows synthesises <c>CF_DIB</c> from it). Copied
    /// files are not counted — telling image files from others means opening the clipboard.
    /// </remarks>
    public bool ContainsImages
    {
        get
        {
            try
            {
                return (_pngFormat != 0 && IsClipboardFormatAvailable(_pngFormat))
                       || IsClipboardFormatAvailable(ClipboardRules.CfDibV5)
                       || IsClipboardFormatAvailable(ClipboardRules.CfDib);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <inheritdoc />
    /// <exception cref="ClipboardBusyException">Another process held the clipboard through every retry.</exception>
    public IReadOnlyList<byte[]> ReadImages()
    {
        try
        {
            if (_disposed || Grab() is not { } raw) return [];
            var images = new List<byte[]>(ClipboardRules.MaxImages);
            switch (raw.Source)
            {
                case ClipboardImageSource.Png:
                    if (DecodeToPng(raw.Data) is { } png) images.Add(png);
                    break;
                case ClipboardImageSource.DibV5:
                case ClipboardImageSource.Dib:
                    if (DecodeDib(raw.Data) is { } dib) images.Add(dib);
                    break;
                case ClipboardImageSource.Files:
                    foreach (var path in ClipboardRules.ImageFiles(ClipboardRules.ParseDropFiles(raw.Data) ?? []))
                    {
                        if (images.Count >= ClipboardRules.MaxImages) break;
                        if (ReadImageFile(path) is { } file) images.Add(file);
                    }
                    break;
            }
            return images;
        }
        catch (Exception e) when (e is not ClipboardBusyException)
        {
            Log($"reading images failed: {e.Message}");
            return [];
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Mirrors Swift's <c>removeImages</c>: image formats go; a copied-file list loses its image
    /// files (and the shell's descriptions of that list, which would no longer match); everything
    /// else is written back verbatim, in the original order. Formats that are not memory blocks
    /// (GDI handles, private formats, OLE's own bookkeeping) cannot be copied and are lost. Nothing
    /// is emptied when there is no image to remove, or when what would have to be put back exceeds
    /// <see cref="ClipboardRules.MaxPreservedBytes"/>.
    /// </remarks>
    public bool ClearIfUnchanged(long sequence)
    {
        try
        {
            if (!_dispatcher.CheckAccess()) return _dispatcher.Invoke(() => ClearIfUnchanged(sequence));
            if (_disposed || GetClipboardSequenceNumber() != sequence) return false;

            var owner = EnsureOwner();
            if (!Open(owner, ClearAttempts))
            {
                Log("not cleared: the clipboard stayed busy");
                return false;
            }
            try
            {
                // Checked again now that nobody else can change it.
                if (GetClipboardSequenceNumber() != sequence) return false;
                if (PlanWriteBack() is not { } keep) return false;
                if (!EmptyClipboard())
                {
                    Log($"not cleared: EmptyClipboard failed ({Marshal.GetLastPInvokeError()})");
                    return false;
                }
                foreach (var (format, data) in keep) Put(format, data);
                return true;
            }
            finally
            {
                CloseClipboard();
            }
        }
        catch (Exception e)
        {
            Log($"clearing failed: {e.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        if (!_dispatcher.CheckAccess())
        {
            try { _dispatcher.Invoke(new Action(Dispose)); }
            catch (Exception) { /* dispatcher gone: the window went with it */ }
            return;
        }
        if (_disposed) return;
        _disposed = true;
        _owner?.Dispose();
        _owner = null;
    }

    // ── Reading ──────────────────────────────────────────────────────────────────────────

    private sealed record Raw(ClipboardImageSource Source, byte[] Data);

    /// <summary>
    /// Copy out the bytes of the best image source (PNG › CF_DIBV5 › CF_DIB › CF_HDROP), holding
    /// the clipboard no longer than that. A source that is listed but will not render (a delayed
    /// render that failed) falls through to the next; one over 20 MB ends the read (Swift skips it).
    /// </summary>
    private Raw? Grab()
    {
        if (!Open(IntPtr.Zero, ReadAttempts))      // NULL owner: fine for reading
        {
            // Thrown, not "no images": the caller retries this change on its next poll.
            Log("not read: the clipboard stayed busy");
            throw new ClipboardBusyException();
        }
        try
        {
            foreach (var source in ClipboardRules.SourcePriority)
            {
                var format = source switch
                {
                    ClipboardImageSource.Png => _pngFormat,
                    ClipboardImageSource.DibV5 => ClipboardRules.CfDibV5,
                    ClipboardImageSource.Dib => ClipboardRules.CfDib,
                    _ => ClipboardRules.CfHdrop,
                };
                if (format == 0 || !IsClipboardFormatAvailable(format)) continue;
                var limit = source == ClipboardImageSource.Files ? ClipboardRules.MaxFileListBytes : ClipboardRules.MaxSourceBytes;
                var data = CopyBlock(GetClipboardData(format), limit, out var tooBig);
                if (tooBig)
                {
                    Log($"skipped: the {source} image is over the size limit");
                    return null;
                }
                if (data is not null) return new Raw(source, data);
            }
            return null;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static byte[]? ReadImageFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || !ClipboardRules.IsWithinSourceLimit(info.Length)) return null;
            var bytes = File.ReadAllBytes(path);
            return ClipboardRules.IsWithinSourceLimit(bytes.LongLength) ? DecodeToPng(bytes) : null;
        }
        catch (Exception e)
        {
            Log($"a copied image file could not be read: {e.GetType().Name}");     // the path is the user's; not logged
            return null;
        }
    }

    /// <summary>A packed DIB → PNG. 32-bit DIBs are read here (alpha rules in <see cref="ClipboardRules.Dib32ToBgra"/>); the rest by the system BMP decoder.</summary>
    private static byte[]? DecodeDib(byte[] dib)
    {
        try
        {
            if (ClipboardRules.ParseDib(dib) is not { } info) return null;
            if (!ClipboardRules.IsWithinPixelLimit(info.Width, info.Height)) return null;
            if (info.Compression is ClipboardRules.BiJpeg or ClipboardRules.BiPng)
                return DecodeToPng(dib.AsSpan(info.PixelOffset, info.SizeImage).ToArray());
            if (ClipboardRules.Dib32ToBgra(dib, info) is { } pixels)
            {
                var format = pixels.Alpha switch
                {
                    DibAlpha.Straight => PixelFormats.Bgra32,
                    DibAlpha.Premultiplied => PixelFormats.Pbgra32,
                    _ => PixelFormats.Bgr32,
                };
                var bitmap = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, format, null, pixels.Bgra, pixels.Stride);
                return ToPng(bitmap);
            }
            return DecodeToPng(ClipboardRules.DibToBmpFile(dib, info));
        }
        catch (Exception e)
        {
            Log($"a clipboard bitmap could not be decoded: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Any encoded image the system can decode (PNG, JPEG, BMP, GIF, TIFF; HEIC and WebP where
    /// their codecs are installed) → PNG. A colour profile the colour system rejects is ignored
    /// on a second try rather than losing the image.
    /// </summary>
    private static byte[]? DecodeToPng(byte[] data)
    {
        foreach (var options in new[] { BitmapCreateOptions.None, BitmapCreateOptions.IgnoreColorProfile })
        {
            try
            {
                // Decoded lazily (CacheOption.None) through the scale and the encoder, so a small
                // file claiming huge dimensions is refused before any pixel is held. The stream
                // must stay open until Save, hence the using around the whole pipeline.
                using var stream = new MemoryStream(data, writable: false);
                var decoder = BitmapDecoder.Create(stream, options, BitmapCacheOption.None);
                if (decoder.Frames.Count == 0) return null;
                return ToPng(decoder.Frames[0]);
            }
            catch (Exception e) when (options == BitmapCreateOptions.None)
            {
                _ = e;      // retried without the colour profile
            }
            catch (Exception e)
            {
                Log($"an image could not be decoded: {e.GetType().Name}");
            }
        }
        return null;
    }

    /// <summary>
    /// Re-encode as PNG with the longest side ≤ 2048 px (Swift <c>downscaledPNG</c>): scaled in
    /// premultiplied (or opaque) 32-bit so transparent edges do not fringe, written as 24-bit RGB
    /// when opaque and 32-bit RGBA when not.
    /// </summary>
    private static byte[]? ToPng(BitmapSource source)
    {
        if (!ClipboardRules.IsWithinPixelLimit(source.PixelWidth, source.PixelHeight)) return null;
        var alpha = HasAlpha(source);

        BitmapSource image = Convert(source, alpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32);
        var (width, height) = ClipboardRules.FitWithin(image.PixelWidth, image.PixelHeight);
        if (width != image.PixelWidth || height != image.PixelHeight)
        {
            image = new TransformedBitmap(image, new ScaleTransform((double)width / image.PixelWidth, (double)height / image.PixelHeight));
            // TransformedBitmap rounds the scaled size itself; never let it exceed the limit by a pixel.
            if (image.PixelWidth > width || image.PixelHeight > height)
                image = new CroppedBitmap(image, new Int32Rect(0, 0, Math.Min(width, image.PixelWidth), Math.Min(height, image.PixelHeight)));
        }
        image = Convert(image, alpha ? PixelFormats.Bgra32 : PixelFormats.Bgr24);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static BitmapSource Convert(BitmapSource source, PixelFormat format) =>
        source.Format == format ? source : new FormatConvertedBitmap(source, format, null, 0);

    private static bool HasAlpha(BitmapSource source)
    {
        var f = source.Format;
        if (f == PixelFormats.Bgra32 || f == PixelFormats.Pbgra32 || f == PixelFormats.Rgba64 || f == PixelFormats.Prgba64
            || f == PixelFormats.Rgba128Float || f == PixelFormats.Prgba128Float)
            return true;
        if (f == PixelFormats.Indexed1 || f == PixelFormats.Indexed2 || f == PixelFormats.Indexed4 || f == PixelFormats.Indexed8)
            return source.Palette?.Colors.Any(c => c.A < 255) ?? false;     // a GIF's transparent colour
        return false;
    }

    // ── Clearing ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// With the clipboard open: what to write back after emptying it, or null to leave it alone
    /// (no image on it, or more to preserve than <see cref="ClipboardRules.MaxPreservedBytes"/>).
    /// </summary>
    private static List<(uint Format, byte[] Data)>? PlanWriteBack()
    {
        var formats = new List<(uint Format, ClipboardFormatKind Kind)>();
        for (var f = EnumClipboardFormats(0); f != 0; f = EnumClipboardFormats(f))
            formats.Add((f, ClipboardRules.Classify(f, f >= 0xC000 ? FormatName(f) : null)));

        // The file list first: whether it holds images decides what happens to it and its companions.
        var filesChanged = false;
        byte[]? remainingFiles = null;
        if (formats.Any(x => x.Kind == ClipboardFormatKind.Files)
            && CopyBlock(GetClipboardData(ClipboardRules.CfHdrop), ClipboardRules.MaxFileListBytes, out _) is { } drop
            && ClipboardRules.ParseDropFiles(drop) is { } paths
            && paths.Any(ClipboardRules.IsImageFileName))
        {
            filesChanged = true;
            var rest = paths.Where(p => !ClipboardRules.IsImageFileName(p)).ToList();
            if (rest.Count > 0) remainingFiles = ClipboardRules.BuildDropFiles(rest);
        }

        if (!filesChanged && !formats.Any(x => x.Kind == ClipboardFormatKind.Image)) return null;

        var keep = new List<(uint, byte[])>();
        long total = 0;
        foreach (var (format, kind) in formats)
        {
            switch (kind)
            {
                case ClipboardFormatKind.Image:
                case ClipboardFormatKind.Uncopyable:
                    continue;
                case ClipboardFormatKind.Files when filesChanged:
                    if (remainingFiles is not null) keep.Add((format, remainingFiles));
                    continue;
                case ClipboardFormatKind.FileCompanion when filesChanged:
                    continue;
            }
            // Opaque bytes: whatever this format holds (text included) is copied, not read.
            if (CopyBlock(GetClipboardData(format), long.MaxValue, out _) is not { } data) continue;
            total += data.LongLength;
            if (total > ClipboardRules.MaxPreservedBytes)
            {
                Log("not cleared: too much else on the clipboard to put back");
                return null;
            }
            keep.Add((format, data));
        }
        return keep;
    }

    /// <summary>Hand <paramref name="data"/> to the clipboard as a new moveable block (the system owns it once set).</summary>
    private static void Put(uint format, byte[] data)
    {
        var block = GlobalAlloc(MoveableMemory, (nuint)data.Length);
        if (block == IntPtr.Zero) return;
        var pointer = GlobalLock(block);
        if (pointer == IntPtr.Zero)
        {
            GlobalFree(block);
            return;
        }
        try
        {
            Marshal.Copy(data, 0, pointer, data.Length);
        }
        finally
        {
            GlobalUnlock(block);
        }
        if (SetClipboardData(format, block) == IntPtr.Zero) GlobalFree(block);
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The bytes of a clipboard <c>HGLOBAL</c> (owned by the clipboard: not freed here). Null for
    /// no handle, an empty block or a handle that is not a memory block (<c>GlobalSize</c>
    /// validates the handle and answers 0); <paramref name="tooBig"/> when over
    /// <paramref name="limit"/>. Only valid while the clipboard is open.
    /// </summary>
    private static byte[]? CopyBlock(IntPtr handle, long limit, out bool tooBig)
    {
        tooBig = false;
        if (handle == IntPtr.Zero) return null;
        var size = GlobalSize(handle);
        if (size == 0) return null;
        if (size > (ulong)Math.Min(limit, int.MaxValue))
        {
            tooBig = true;
            return null;
        }
        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero) return null;
        try
        {
            var bytes = new byte[(int)size];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static string? FormatName(uint format)
    {
        var buffer = new char[256];
        var length = GetClipboardFormatName(format, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : null;
    }

    private static bool Open(IntPtr owner, int attempts)
    {
        for (var i = 0; i < attempts; i++)
        {
            if (OpenClipboard(owner)) return true;
            Thread.Sleep(RetryDelayMs);
        }
        return false;
    }

    /// <summary>
    /// The window that owns the clipboard after a write-back. <c>EmptyClipboard</c> after
    /// <c>OpenClipboard(NULL)</c> leaves the clipboard ownerless and every <c>SetClipboardData</c>
    /// then fails, so an owner is required. Message-only, on the UI thread.
    /// </summary>
    private IntPtr EnsureOwner()
    {
        _owner ??= new HwndSource(new HwndSourceParameters("LocalCaption clipboard")
        {
            ParentWindow = Win32.MessageOnlyParent,
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        });
        return _owner.Handle;
    }

    private static void Log(string line) => InterviewPlatformLog.Write("clipboard", line);

    private const uint MoveableMemory = 0x0002;     // GMEM_MOVEABLE: required for clipboard data

    /// <summary>Windows 2000+. Never opens the clipboard.</summary>
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    /// <summary>Windows 2000+. Never opens the clipboard.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    /// <summary>Windows 2000+. Same name → same id in every process for the session.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClipboardFormatW")]
    private static extern uint RegisterClipboardFormat(string name);

    /// <summary>Windows 2000+.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClipboardFormatNameW")]
    private static extern int GetClipboardFormatName(uint format, [Out] char[] name, int maxCount);

    /// <summary>Windows 2000+. Fails while another window has it open.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    /// <summary>Windows 2000+.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    /// <summary>Windows 2000+. Makes the window passed to <c>OpenClipboard</c> the owner.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    /// <summary>Windows 2000+. 0 ends the enumeration (or reports an error).</summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint EnumClipboardFormats(uint format);

    /// <summary>Windows 2000+. May ask the owner to render a delayed format; null if it cannot.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    /// <summary>Windows 2000+. On success the system owns <paramref name="memory"/>.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    /// <summary>Windows 2000+.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);

    /// <summary>Windows 2000+.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);

    /// <summary>Windows 2000+.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    /// <summary>Windows 2000+.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);

    /// <summary>Windows 2000+. 0 for an invalid handle.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint GlobalSize(IntPtr memory);
}

/// <summary>
/// The clipboard stayed open in another process through every retry. <see cref="ClipboardImages.ReadImages"/>
/// throws it so <see cref="InterviewController.PollClipboard()"/> keeps the old sequence number
/// and reads the same change again on its next poll (up to five tries).
/// </summary>
public sealed class ClipboardBusyException() : IOException("The clipboard is in use by another application.");
