using LocalCaption.Core.Audio;

namespace LocalCaption.Core.Tests;

/// <summary>
/// The §4.3 sample clock. No macOS counterpart — <c>ScreenCaptureKit</c> delivers continuous
/// buffers including silence, so this whole problem is a Windows one.
/// </summary>
/// <remarks>
/// Each case here is a behaviour B0 measured on the G15 (<c>windows/BENCH-RESULTS.md</c> §3)
/// or one the measurements imply. The reason this logic is tested at all is that its failure
/// mode is silent: a stalled clock produces a transcript that looks fine and is wrong from
/// the first pause onward.
/// </remarks>
public sealed class SampleClockTests
{
    private const int Rate = 48000;

    /// <summary>One 10 ms WASAPI packet at the endpoint mix rate.</summary>
    private const int Packet = 480;

    [Fact]
    public void FirstPacketDefinesTheOriginAndPadsNothing()
    {
        var clock = new SampleClock(Rate);
        clock.Rebase(now: 0);

        // A device that has been rendering for an hour before Start must not contribute an
        // hour of silence to the session.
        Assert.Equal(0, clock.OnPacket(devicePosition: 9_999_999, frames: Packet, now: 0));
        Assert.Equal(0, clock.InsertedFrames);
    }

    [Fact]
    public void ContiguousPacketsNeedNoCorrection()
    {
        var clock = new SampleClock(Rate);
        clock.Rebase(now: 0);
        var position = 1000L;
        var now = 0.0;

        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(0, clock.OnPacket(position, Packet, now));
            position += Packet;
            now += 0.01;
        }

        Assert.Equal(0, clock.InsertedFrames);
        Assert.Equal(0, clock.Regressions);
    }

    [Fact]
    public void DevicePositionJumpIsPaddedExactly()
    {
        var clock = new SampleClock(Rate);
        clock.Rebase(now: 0);
        clock.OnPacket(1000, Packet, 0);

        // The engine rendered 500 ms we never received, then handed us the next packet.
        var missing = Rate / 2;
        var padding = clock.OnPacket(1000 + Packet + missing, Packet, 0.51);

        Assert.Equal(missing, padding);
        Assert.Equal(missing, clock.PaddedFrames);
        Assert.Equal(0, clock.SynthesizedFrames);
    }

    [Fact]
    public void IdleIsSilentUntilTheThresholdIsCrossed()
    {
        var clock = new SampleClock(Rate, idleThresholdSeconds: 0.2);
        clock.Rebase(now: 0);
        clock.OnPacket(0, Packet, 0);

        // Ordinary jitter between packets must not manufacture silence.
        Assert.Equal(0, clock.OnIdle(0.05));
        Assert.Equal(0, clock.OnIdle(0.19));
        Assert.Equal(0, clock.SynthesizedFrames);

        Assert.True(clock.OnIdle(0.25) > 0);
    }

    [Fact]
    public void NoPacketsAtAllStillAdvancesTheClock()
    {
        // The measured case: the Jump Desktop Virtual Speaker with nothing rendering
        // produced zero packets for twelve seconds. Device-position padding cannot help —
        // no packet ever arrives carrying a position — so the wall clock has to.
        var clock = new SampleClock(Rate, idleThresholdSeconds: 0.2);
        clock.Rebase(now: 0);
        clock.OnPacket(0, Packet, 0);

        var emitted = 0L;
        for (var tick = 1; tick <= 1200; tick++) emitted += clock.OnIdle(tick * 0.01);

        Assert.Equal(0, clock.PaddedFrames);
        Assert.Equal(12 * Rate, emitted, tolerance: Rate / 100);
        Assert.Equal(emitted, clock.SynthesizedFrames);
    }

    [Fact]
    public void CrossingTheThresholdLateLosesNoTime()
    {
        // The stall is only noticed once, five seconds in. The whole shortfall must still
        // be recovered — a late check costs accuracy nowhere.
        var clock = new SampleClock(Rate, idleThresholdSeconds: 0.2);
        clock.Rebase(now: 0);
        clock.OnPacket(0, Packet, 0);

        Assert.Equal(5 * Rate, clock.OnIdle(5.0), tolerance: 1);
    }

    [Fact]
    public void AGapIsNeverCountedTwice()
    {
        // The reconciliation that matters: the wall clock covered a stall, and then the
        // device came back reporting the same stall in its own numbering. Emitting both
        // would stretch the session and push every later timestamp out.
        var clock = new SampleClock(Rate, idleThresholdSeconds: 0.2);
        clock.Rebase(now: 0);
        clock.OnPacket(1000, Packet, 0);

        var synthesized = clock.OnIdle(1.0);
        Assert.Equal(Rate, synthesized, tolerance: 1);

        // The device agrees that one second passed, and says so through its position.
        var padding = clock.OnPacket(1000 + Packet + Rate, Packet, 1.0);

        Assert.Equal(0, padding);
        Assert.Equal(Rate, clock.InsertedFrames, tolerance: 1);
    }

    [Fact]
    public void ADeviceGapLongerThanTheStallIsToppedUp()
    {
        // The wall clock covered one second; the device reports two. The extra second is
        // real and still owed.
        var clock = new SampleClock(Rate, idleThresholdSeconds: 0.2);
        clock.Rebase(now: 0);
        clock.OnPacket(1000, Packet, 0);

        clock.OnIdle(1.0);
        var padding = clock.OnPacket(1000 + Packet + 2 * Rate, Packet, 1.0);

        Assert.Equal(Rate, padding, tolerance: 1);
        Assert.Equal(2 * Rate, clock.InsertedFrames, tolerance: 1);
    }

    [Fact]
    public void SynthesizedFramesDoNotShiftTheDeviceNumbering()
    {
        // Frames we invented are not frames the engine counted. If they were folded into
        // the expected position, the next genuine packet would look like a regression and
        // every later gap would be mismeasured.
        var clock = new SampleClock(Rate, idleThresholdSeconds: 0.2);
        clock.Rebase(now: 0);
        clock.OnPacket(1000, Packet, 0);
        clock.OnIdle(1.0);
        clock.OnPacket(1000 + Packet, Packet, 1.0);

        Assert.Equal(0, clock.Regressions);
        Assert.Equal(0, clock.OnPacket(1000 + 2 * Packet, Packet, 1.01));
    }

    [Fact]
    public void BackwardsPositionIsCountedAndNeverPadded()
    {
        // A virtual driver is where this would appear. It must not be turned into a
        // negative correction, and it must be visible rather than absorbed.
        var clock = new SampleClock(Rate);
        clock.Rebase(now: 0);
        clock.OnPacket(10_000, Packet, 0);
        var padding = clock.OnPacket(5_000, Packet, 0.01);

        Assert.Equal(0, padding);
        Assert.Equal(1, clock.Regressions);
        Assert.Equal(0, clock.PaddedFrames);
    }

    [Fact]
    public void RebaseAdoptsTheNewDevicesOrigin()
    {
        // §4.2: the default endpoint changed mid-session and the client was rebuilt. The new
        // device counts from its own origin, so its position is not a gap — an eleven-hour
        // one, here. Only the real time spent switching is owed, and that is zero in this
        // case because no clock time passed.
        var clock = new SampleClock(Rate);
        clock.Rebase(now: 0);
        clock.OnPacket(1000, Packet, 0);
        clock.Rebase(now: 0);

        Assert.Equal(0, clock.OnPacket(2_000_000_000, Packet, 0));
        Assert.Equal(0, clock.PaddedFrames);
    }

    [Fact]
    public void IdleBeforeTheSessionStartsDoesNothing()
    {
        // Nobody has pressed Start. Elapsed time is not the session's to account for.
        var clock = new SampleClock(Rate);
        Assert.Equal(0, clock.OnIdle(10.0));
        Assert.Equal(0, clock.SynthesizedFrames);
    }

    [Fact]
    public void AStreamThatNeverDeliversAPacketStillKeepsTime()
    {
        // The measured case, and the one that caught a real bug in B1: with the keepalive
        // off, the Jump Desktop Virtual Speaker produces no packets at all — not a late
        // first packet, none. A clock that starts on the first packet therefore never
        // starts, and fifteen seconds of session vanish while everything reports healthy.
        var clock = new SampleClock(Rate, idleThresholdSeconds: 0.2);
        clock.Rebase(now: 0);

        var emitted = 0L;
        for (var tick = 1; tick <= 1500; tick++) emitted += clock.OnIdle(tick * 0.01);

        Assert.Equal(15 * Rate, emitted, tolerance: Rate / 100);
        Assert.Equal(emitted, clock.SynthesizedFrames);
    }

    [Fact]
    public void AFirstPacketAfterSilenceKeepsWhatWasAlreadySynthesised()
    {
        // Audio finally starts three seconds in. Those three seconds happened and stay on
        // the clock; the packet only defines where the device's own numbering begins.
        var clock = new SampleClock(Rate, idleThresholdSeconds: 0.2);
        clock.Rebase(now: 0);
        clock.OnIdle(3.0);

        Assert.Equal(0, clock.OnPacket(devicePosition: 777_000, frames: Packet, now: 3.0));
        Assert.Equal(3 * Rate, clock.SynthesizedFrames, tolerance: 1);
        Assert.Equal(0, clock.PaddedFrames);
    }

    [Fact]
    public void ADeviceChangeMidSessionKeepsTheSessionClockRunning()
    {
        // §4.2: the default endpoint moved and the client was rebuilt. The switch takes
        // real time, the session did not pause, and the new device's origin is unrelated
        // to the old one's.
        var clock = new SampleClock(Rate, idleThresholdSeconds: 0.2);
        clock.Rebase(now: 0);
        clock.OnPacket(1000, Packet, 0.1);

        clock.Rebase(now: 0.1);                       // tear down and re-open
        var duringSwitch = clock.OnIdle(0.9);
        Assert.True(duringSwitch > 0, "the switchover gap must still be owed");

        Assert.Equal(0, clock.OnPacket(5_000_000, Packet, 0.9));
        Assert.Equal(0, clock.PaddedFrames);
    }

    [Fact]
    public void AStreamThatNeverReportsAPositionIsRecognised()
    {
        // Measured in B1: process loopback — the v1 default capture mode — returns 0 for
        // u64DevicePosition on every packet. Counting each one as a backwards jump would
        // bury the genuine signal under thousands of false ones, and §4.3's padding has
        // nothing to work with either way.
        var clock = new SampleClock(Rate);
        clock.Rebase(now: 0);

        for (var i = 0; i < 50; i++) Assert.Equal(0, clock.OnPacket(devicePosition: 0, frames: Packet, now: i * 0.01));

        Assert.False(clock.PositionReported);
        Assert.Equal(0, clock.Regressions);
        Assert.Equal(0, clock.PaddedFrames);
    }

    [Fact]
    public void TheWallClockStillCoversAStreamWithNoPosition()
    {
        // Which leaves the wall-clock rule as the whole of mode A's protection.
        var clock = new SampleClock(Rate, idleThresholdSeconds: 0.2);
        clock.Rebase(now: 0);
        for (var i = 0; i < 10; i++) clock.OnPacket(0, Packet, i * 0.01);

        Assert.False(clock.PositionReported);
        Assert.Equal(2 * Rate, clock.OnIdle(2.09), tolerance: Rate / 50);
    }

    [Fact]
    public void AGenuineZeroAtTheStartIsNotMistakenForAMissingPosition()
    {
        // An endpoint whose counter happens to start at zero must not be written off — the
        // position is only disbelieved once it has stayed there across several packets.
        var clock = new SampleClock(Rate);
        clock.Rebase(now: 0);

        clock.OnPacket(devicePosition: 0, frames: Packet, now: 0);
        var padding = clock.OnPacket(devicePosition: Packet + Rate, frames: Packet, now: 1.0);

        Assert.True(clock.PositionReported);
        Assert.Equal(Rate, padding);
    }

    [Fact]
    public void TheGapAcrossADeviceChangeIsPaidForAtTheNewStreamsFirstPacket()
    {
        // §4.2 measured: the default endpoint moved mid-capture, the client was rebuilt, and
        // the third of a second that took vanished from the clock — the new stream's first
        // packet adopted its origin and reset the accounting without owning the gap.
        var clock = new SampleClock(Rate, idleThresholdSeconds: 0.2);
        clock.Rebase(now: 0);
        clock.OnPacket(1000, Packet, 0);            // last packet of the old endpoint

        clock.Rebase(now: 0.5, sampleRate: Rate);   // tear down and re-open elsewhere
        var owed = clock.OnPacket(devicePosition: 9_000_000, frames: Packet, now: 0.33);

        Assert.Equal(0.33 * Rate, owed, tolerance: Rate / 50);
        Assert.Equal(owed, clock.SynthesizedFrames, tolerance: 1);
    }

    [Fact]
    public void TheWaitForTheFirstPacketIsPaidForToo()
    {
        // Same rule at session start: opening a stream takes real time, and the session's
        // clock starts when it opens, not when audio happens to begin.
        var clock = new SampleClock(Rate, idleThresholdSeconds: 0.2);
        clock.Rebase(now: 0);

        Assert.Equal(0.1 * Rate, clock.OnPacket(1000, Packet, 0.1), tolerance: Rate / 100);
    }

    [Fact]
    public void HaltStopsTheClockOwingAnything()
    {
        var clock = new SampleClock(Rate, idleThresholdSeconds: 0.2);
        clock.Rebase(now: 0);
        clock.Halt();

        Assert.Equal(0, clock.OnIdle(10.0));
    }
}
