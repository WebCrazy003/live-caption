namespace LocalCaption.Interview;

/// <summary>
/// A rectangle in physical screen pixels (virtual-desktop coordinates, as Win32 <c>RECT</c>):
/// <see cref="Left"/>/<see cref="Top"/> inclusive, <see cref="Right"/>/<see cref="Bottom"/> exclusive.
/// </summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Right <= Left || Bottom <= Top;

    public static PixelRect FromSize(int left, int top, int width, int height) => new(left, top, left + width, top + height);

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    /// <summary>The overlap; <see cref="IsEmpty"/> when there is none.</summary>
    public PixelRect Intersect(PixelRect other)
    {
        var r = new PixelRect(Math.Max(Left, other.Left), Math.Max(Top, other.Top),
                              Math.Min(Right, other.Right), Math.Min(Bottom, other.Bottom));
        return r.IsEmpty ? default : r;
    }

    /// <summary>The smallest rectangle holding both; an empty side is ignored.</summary>
    public PixelRect Union(PixelRect other) =>
        IsEmpty ? other
        : other.IsEmpty ? this
        : new(Math.Min(Left, other.Left), Math.Min(Top, other.Top), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));

    public PixelRect Offset(int dx, int dy) => new(Left + dx, Top + dy, Right + dx, Bottom + dy);
}

/// <summary>A top-level window as the Space key sees it: its frame, and whether it is the desktop.</summary>
/// <param name="Bounds">The visible frame (<c>DWMWA_EXTENDED_FRAME_BOUNDS</c>), physical pixels.</param>
/// <param name="IsDesktop">The shell's desktop window, which spans every monitor: cut to the one under the pointer.</param>
public readonly record struct WindowTarget(PixelRect Bounds, bool IsDesktop);

/// <summary>
/// The arithmetic of the region screenshot (SPEC-16 §4.3), kept free of WPF and Win32 so it is
/// tested on the Mac. Everything is in physical pixels except the drag points, which arrive in
/// the overlay's device-independent units.
/// </summary>
public static class CaptureGeometry
{
    /// <summary>A drag smaller than this (either side, physical pixels) is a click, not a selection.</summary>
    public const int MinSelectionSide = 4;

    /// <summary>The bounding box of every monitor — the frozen frame's extent.</summary>
    public static PixelRect UnionOf(IEnumerable<PixelRect> rects) =>
        rects.Aggregate(default(PixelRect), (acc, r) => acc.Union(r));

    /// <summary>
    /// The physical rectangle a drag selects on one monitor's overlay.
    /// </summary>
    /// <param name="x0">Drag start, overlay units (WPF DIPs) from the overlay's top-left.</param>
    /// <param name="y0">Drag start.</param>
    /// <param name="x1">Pointer now.</param>
    /// <param name="y1">Pointer now.</param>
    /// <param name="scaleX">Physical pixels per overlay unit: the monitor's pixel width over the overlay's width.
    /// A ratio, not a DPI, so it is right whatever DPI awareness the overlay window got.</param>
    /// <param name="scaleY">The same vertically.</param>
    /// <param name="monitor">The monitor's physical rectangle; the result is clamped to it.</param>
    /// <returns>Outward-rounded, in virtual-desktop coordinates; empty when the drag left the monitor entirely.</returns>
    public static PixelRect FromDrag(double x0, double y0, double x1, double y1, double scaleX, double scaleY, PixelRect monitor)
    {
        var left = (int)Math.Floor(Math.Min(x0, x1) * scaleX);
        var top = (int)Math.Floor(Math.Min(y0, y1) * scaleY);
        var right = (int)Math.Ceiling(Math.Max(x0, x1) * scaleX);
        var bottom = (int)Math.Ceiling(Math.Max(y0, y1) * scaleY);
        return new PixelRect(left, top, right, bottom).Offset(monitor.Left, monitor.Top).Intersect(monitor);
    }

    /// <summary>Whether a dragged rectangle is big enough to be a selection rather than a stray click.</summary>
    public static bool IsSelection(PixelRect r) => r.Width >= MinSelectionSide && r.Height >= MinSelectionSide;

    /// <summary>
    /// What Space captures: the frontmost window under <paramref name="x"/>,<paramref name="y"/>
    /// (<paramref name="zOrder"/> is front to back, as <c>EnumWindows</c> lists them), cut to the
    /// frozen <paramref name="frame"/> — or, for the desktop, to <paramref name="monitor"/>.
    /// Null when nothing is there.
    /// </summary>
    public static PixelRect? WindowAt(IReadOnlyList<WindowTarget> zOrder, int x, int y, PixelRect frame, PixelRect monitor)
    {
        foreach (var w in zOrder)
        {
            if (!w.Bounds.Contains(x, y)) continue;
            var r = w.Bounds.Intersect(w.IsDesktop ? monitor : frame);
            return IsSelection(r) ? r : null;
        }
        return null;
    }

    /// <summary>The monitor holding a point, else the first one (the pointer can sit in a gap between monitors).</summary>
    public static int MonitorAt(IReadOnlyList<PixelRect> monitors, int x, int y)
    {
        for (var i = 0; i < monitors.Count; i++)
            if (monitors[i].Contains(x, y)) return i;
        return 0;
    }
}
