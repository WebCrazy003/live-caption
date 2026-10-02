namespace LocalCaption.Interview.Tests;

/// <summary>The region screenshot's arithmetic (SPEC-16 §4.3): physical pixels on mixed-DPI monitors.</summary>
public sealed class CaptureGeometryTests
{
    // A 1920×1080 monitor at 100 % left of a 3840×2160 primary at 150 %, top-aligned.
    private static readonly PixelRect Left = new(-1920, 0, 0, 1080);
    private static readonly PixelRect Primary = new(0, 0, 3840, 2160);

    [Fact]
    public void UnionCoversEveryMonitorIncludingNegativeCoordinates()
    {
        Assert.Equal(new PixelRect(-1920, 0, 3840, 2160), CaptureGeometry.UnionOf([Left, Primary]));
        Assert.Equal(new PixelRect(0, -1200, 3840, 2160), CaptureGeometry.UnionOf([Primary, new PixelRect(0, -1200, 1920, 0)]));
        Assert.True(CaptureGeometry.UnionOf([]).IsEmpty);
    }

    [Fact]
    public void DragMapsOverlayUnitsToPhysicalPixelsByRatio()
    {
        // The 4K monitor's overlay is 2560 DIPs wide at 150 %: 1.5 px per unit.
        var r = CaptureGeometry.FromDrag(100, 50, 300, 250, 1.5, 1.5, Primary);
        Assert.Equal(new PixelRect(150, 75, 450, 375), r);
    }

    [Fact]
    public void DragOnAMonitorLeftOfThePrimaryIsOffsetIntoVirtualCoordinates()
    {
        var r = CaptureGeometry.FromDrag(10, 20, 110, 220, 1.0, 1.0, Left);
        Assert.Equal(new PixelRect(-1910, 20, -1810, 220), r);
    }

    [Fact]
    public void DragDirectionDoesNotMatter()
    {
        Assert.Equal(CaptureGeometry.FromDrag(300, 250, 100, 50, 1.5, 1.5, Primary),
                     CaptureGeometry.FromDrag(100, 50, 300, 250, 1.5, 1.5, Primary));
    }

    [Fact]
    public void FractionalEdgesRoundOutward()
    {
        // 10.5 × 1.25 = 13.125 → floor 13, 20.1 × 1.25 = 25.125 → floor 25; 30.3 × 1.25 = 37.875 → ceil 38.
        var r = CaptureGeometry.FromDrag(10.5, 20.1, 30.3, 40.0, 1.25, 1.25, Primary);
        Assert.Equal(new PixelRect(13, 25, 38, 50), r);
    }

    [Fact]
    public void DragPastTheEdgeIsClampedToTheMonitor()
    {
        var r = CaptureGeometry.FromDrag(-50, -50, 5000, 5000, 1.0, 1.0, Left);
        Assert.Equal(Left, r);
    }

    [Fact]
    public void ATinyDragIsAClick()
    {
        Assert.False(CaptureGeometry.IsSelection(CaptureGeometry.FromDrag(10, 10, 11, 30, 1, 1, Primary)));
        Assert.True(CaptureGeometry.IsSelection(CaptureGeometry.FromDrag(10, 10, 14, 14, 1, 1, Primary)));
    }

    [Fact]
    public void SpacePicksTheFrontmostWindowUnderThePointer()
    {
        var frame = CaptureGeometry.UnionOf([Left, Primary]);
        List<WindowTarget> z =
        [
            new(new PixelRect(100, 100, 300, 300), false),        // front
            new(new PixelRect(50, 50, 1000, 800), false),
            new(new PixelRect(-1920, 0, 3840, 2160), true),       // the desktop, behind everything
        ];
        Assert.Equal(new PixelRect(100, 100, 300, 300), CaptureGeometry.WindowAt(z, 150, 150, frame, Primary));
        Assert.Equal(new PixelRect(50, 50, 1000, 800), CaptureGeometry.WindowAt(z, 600, 600, frame, Primary));
    }

    [Fact]
    public void TheDesktopIsCutToTheMonitorUnderThePointer()
    {
        var frame = CaptureGeometry.UnionOf([Left, Primary]);
        List<WindowTarget> z = [new(frame, true)];
        Assert.Equal(Left, CaptureGeometry.WindowAt(z, -500, 500, frame, Left));
    }

    [Fact]
    public void AWindowSpanningMonitorsIsKeptWholeButCutToTheFrame()
    {
        var frame = CaptureGeometry.UnionOf([Left, Primary]);
        List<WindowTarget> z = [new(new PixelRect(-500, -40, 600, 400), false)];     // a little above the top edge
        Assert.Equal(new PixelRect(-500, 0, 600, 400), CaptureGeometry.WindowAt(z, 0, 100, frame, Primary));
    }

    [Fact]
    public void NothingUnderThePointerIsNoCapture()
    {
        List<WindowTarget> z = [new(new PixelRect(100, 100, 300, 300), false)];
        Assert.Null(CaptureGeometry.WindowAt(z, 10, 10, Primary, Primary));
        Assert.Null(CaptureGeometry.WindowAt([], 10, 10, Primary, Primary));
    }

    [Fact]
    public void MonitorAtFallsBackToTheFirstInAGap()
    {
        List<PixelRect> monitors = [Primary, new PixelRect(3840, 500, 5760, 1580)];
        Assert.Equal(1, CaptureGeometry.MonitorAt(monitors, 4000, 600));
        Assert.Equal(0, CaptureGeometry.MonitorAt(monitors, 4000, 100));     // above the second, right of the first
    }

    [Fact]
    public void RectangleOperations()
    {
        var a = new PixelRect(0, 0, 10, 10);
        Assert.Equal(new PixelRect(5, 5, 10, 10), a.Intersect(new PixelRect(5, 5, 20, 20)));
        Assert.True(a.Intersect(new PixelRect(10, 0, 20, 10)).IsEmpty);      // touching edges do not overlap
        Assert.True(a.Contains(0, 0));
        Assert.False(a.Contains(10, 5));                                      // right edge exclusive
        Assert.Equal(new PixelRect(2, 3, 12, 13), a.Offset(2, 3));
        Assert.Equal(PixelRect.FromSize(1, 2, 3, 4), new PixelRect(1, 2, 4, 6));
    }
}
