using MouseDebounceFix.Core;

namespace MouseDebounceFix.Tests;

public sealed class DebounceEngineTests
{
    [Fact]
    public void CleanSingleClick_AllowsDownAndUpImmediately()
    {
        var engine = new DebounceEngine(thresholdMs: 65.0);

        var down = engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Down, 100.0);
        var up = engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Up, 175.0);

        Assert.True(down.ShouldAllow);
        Assert.Equal(DebounceReason.Allowed, down.Reason);
        Assert.Null(down.ElapsedSinceLastUpMs);

        Assert.True(up.ShouldAllow);
        Assert.Equal(DebounceReason.Allowed, up.Reason);
        Assert.Equal(0, engine.TotalBlockedClicks);
    }

    [Fact]
    public void FirstClickAtTimestampZero_IsAllowed()
    {
        var engine = new DebounceEngine(thresholdMs: 65.0);

        var down = engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Down, 0.0);
        var up = engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Up, 40.0);

        Assert.True(down.ShouldAllow);
        Assert.True(up.ShouldAllow);
        Assert.Equal(0, engine.TotalBlockedClicks);
    }

    [Fact]
    public void FastBounce25Ms_BlocksBounceDownAndOrphanUp_CountsOnce()
    {
        var engine = new DebounceEngine(thresholdMs: 65.0);

        // Legitimate first click
        Assert.True(engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Down, 100.0).ShouldAllow);
        Assert.True(engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Up, 180.0).ShouldAllow);

        // Mechanical switch chatter 25 ms after release
        var bounceDown = engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Down, 205.0);
        var bounceUp = engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Up, 220.0);

        Assert.True(bounceDown.ShouldBlock);
        Assert.Equal(DebounceReason.BounceDown, bounceDown.Reason);
        Assert.Equal(25.0, bounceDown.ElapsedSinceLastUpMs);

        Assert.True(bounceUp.ShouldBlock);
        Assert.Equal(DebounceReason.SuppressedOrphanUp, bounceUp.Reason);

        Assert.Equal(1, engine.BlockedLeftClicks);
        Assert.Equal(0, engine.BlockedRightClicks);
        Assert.Equal(1, engine.TotalBlockedClicks);
    }

    [Fact]
    public void TripleConsecutiveBounce_SuppressesEntireBurstAndAllowsNextRealClick()
    {
        var engine = new DebounceEngine(thresholdMs: 65.0);

        // Legitimate initial click
        Assert.True(engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Down, 100.0).ShouldAllow);
        Assert.True(engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Up, 170.0).ShouldAllow);

        // Bounce #1 (25 ms after original Up)
        var b1Down = engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Down, 195.0);
        var b1Up = engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Up, 210.0);
        Assert.Equal(DebounceReason.BounceDown, b1Down.Reason);
        Assert.Equal(DebounceReason.SuppressedOrphanUp, b1Up.Reason);

        // Bounce #2 (30 ms after Bounce #1 Up; 70 ms after original Up — sliding window must still block it)
        var b2Down = engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Down, 240.0);
        var b2Up = engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Up, 255.0);
        Assert.Equal(DebounceReason.BounceDown, b2Down.Reason);
        Assert.Equal(DebounceReason.SuppressedOrphanUp, b2Up.Reason);

        // Bounce #3 (25 ms after Bounce #2 Up)
        var b3Down = engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Down, 280.0);
        var b3Up = engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Up, 295.0);
        Assert.Equal(DebounceReason.BounceDown, b3Down.Reason);
        Assert.Equal(DebounceReason.SuppressedOrphanUp, b3Up.Reason);

        // Subsequent intentional click 180 ms after the burst settled
        var nextRealDown = engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Down, 475.0);
        var nextRealUp = engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Up, 540.0);
        Assert.True(nextRealDown.ShouldAllow);
        Assert.True(nextRealUp.ShouldAllow);

        Assert.Equal(3, engine.BlockedLeftClicks);
        Assert.Equal(3, engine.TotalBlockedClicks);
    }

    [Fact]
    public void NormalHumanDoubleClick180Ms_AllowsBothClicks()
    {
        var engine = new DebounceEngine(thresholdMs: 65.0);

        // First click
        Assert.True(engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Down, 100.0).ShouldAllow);
        Assert.True(engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Up, 170.0).ShouldAllow);

        // Second intentional click 180 ms after release
        var secondDown = engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Down, 350.0);
        var secondUp = engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Up, 420.0);

        Assert.True(secondDown.ShouldAllow);
        Assert.Equal(DebounceReason.Allowed, secondDown.Reason);
        Assert.Equal(180.0, secondDown.ElapsedSinceLastUpMs);

        Assert.True(secondUp.ShouldAllow);
        Assert.Equal(0, engine.TotalBlockedClicks);
    }

    [Fact]
    public void LongDragAndDrop_IsNeverInterrupted_AndFiltersBounceAfterDrop()
    {
        var engine = new DebounceEngine(thresholdMs: 65.0);

        // Press and hold for 3.5 seconds (drag and drop)
        var dragStart = engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Down, 500.0);
        var dragEnd = engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Up, 4000.0);

        Assert.True(dragStart.ShouldAllow);
        Assert.True(dragEnd.ShouldAllow);
        Assert.Equal(0, engine.TotalBlockedClicks);

        // Mechanical bounce 20 ms after releasing the dragged item
        var bounceDown = engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Down, 4020.0);
        var bounceUp = engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Up, 4035.0);

        Assert.True(bounceDown.ShouldBlock);
        Assert.True(bounceUp.ShouldBlock);
        Assert.Equal(1, engine.TotalBlockedClicks);
    }

    [Fact]
    public void ExactThresholdBoundary_AllowsAtThresholdAndBlocksBelow()
    {
        var engine = new DebounceEngine(thresholdMs: 65.0);

        Assert.True(engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Down, 0.0).ShouldAllow);
        Assert.True(engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Up, 100.0).ShouldAllow);

        // 64.99 ms -> blocked
        Assert.True(engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Down, 164.99).ShouldBlock);
        Assert.True(engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Up, 170.0).ShouldBlock);

        // Exactly 65.0 ms after last Up (170 + 65 = 235) -> allowed
        var exactBoundaryDown = engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Down, 235.0);
        Assert.True(exactBoundaryDown.ShouldAllow);
        Assert.Equal(65.0, exactBoundaryDown.ElapsedSinceLastUpMs);
    }

    [Fact]
    public void LeftAndRightButtons_AreTrackedIndependently()
    {
        var engine = new DebounceEngine(thresholdMs: 65.0);

        Assert.True(engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Down, 100.0).ShouldAllow);
        Assert.True(engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Up, 150.0).ShouldAllow);

        // Right click 10 ms after Left release must NOT be blocked
        var rightDown = engine.ProcessEvent(MouseButton.Right, MouseButtonAction.Down, 160.0);
        var rightUp = engine.ProcessEvent(MouseButton.Right, MouseButtonAction.Up, 210.0);

        Assert.True(rightDown.ShouldAllow);
        Assert.True(rightUp.ShouldAllow);

        // Right bounce 20 ms after Right release IS blocked and counted separately
        Assert.True(engine.ProcessEvent(MouseButton.Right, MouseButtonAction.Down, 230.0).ShouldBlock);
        Assert.True(engine.ProcessEvent(MouseButton.Right, MouseButtonAction.Up, 240.0).ShouldBlock);

        Assert.Equal(0, engine.BlockedLeftClicks);
        Assert.Equal(1, engine.BlockedRightClicks);
        Assert.Equal(1, engine.TotalBlockedClicks);
    }

    [Fact]
    public void PausedEngine_NeverBlocksEvents_AndClearsStaleSuppressionState()
    {
        var engine = new DebounceEngine(thresholdMs: 65.0);

        Assert.True(engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Down, 100.0).ShouldAllow);
        Assert.True(engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Up, 150.0).ShouldAllow);

        // Trigger a suppressed bounce Down, then pause before Up arrives
        Assert.True(engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Down, 170.0).ShouldBlock);
        engine.IsPaused = true;

        var upWhilePaused = engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Up, 180.0);
        var rapidDownWhilePaused = engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Down, 190.0);
        Assert.True(upWhilePaused.ShouldAllow);
        Assert.Equal(DebounceReason.Paused, upWhilePaused.Reason);
        Assert.True(rapidDownWhilePaused.ShouldAllow);
        Assert.Equal(DebounceReason.Paused, rapidDownWhilePaused.Reason);

        // Resuming starts clean without inheriting pre-pause timestamps or suppression flags
        engine.IsPaused = false;
        Assert.True(engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Down, 195.0).ShouldAllow);
        Assert.True(engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Up, 230.0).ShouldAllow);
    }

    [Fact]
    public void PerButtonDisable_OnlyFiltersEnabledButtons()
    {
        var engine = new DebounceEngine(thresholdMs: 65.0, filterLeftButton: true, filterRightButton: false);

        Assert.True(engine.ProcessEvent(MouseButton.Right, MouseButtonAction.Down, 100.0).ShouldAllow);
        Assert.True(engine.ProcessEvent(MouseButton.Right, MouseButtonAction.Up, 140.0).ShouldAllow);

        var rapidRight = engine.ProcessEvent(MouseButton.Right, MouseButtonAction.Down, 155.0);
        Assert.True(rapidRight.ShouldAllow);
        Assert.Equal(DebounceReason.ButtonDisabled, rapidRight.Reason);
        Assert.Equal(0, engine.TotalBlockedClicks);
    }

    [Fact]
    public void MissingBounceUp_RecoversCleanlyOnNextValidClick()
    {
        var engine = new DebounceEngine(thresholdMs: 65.0);

        Assert.True(engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Down, 100.0).ShouldAllow);
        Assert.True(engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Up, 150.0).ShouldAllow);

        // Bounce Down is blocked, setting SuppressNextUp = true, but its Up is lost
        Assert.True(engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Down, 170.0).ShouldBlock);

        // Later valid click arrives well past threshold — neither Down nor Up should be swallowed
        var validDown = engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Down, 500.0);
        var validUp = engine.ProcessEvent(MouseButton.Left, MouseButtonAction.Up, 580.0);

        Assert.True(validDown.ShouldAllow);
        Assert.True(validUp.ShouldAllow);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidThreshold_ThrowsArgumentOutOfRangeException(double invalidThreshold)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DebounceEngine(invalidThreshold));

        var engine = new DebounceEngine();
        Assert.Throws<ArgumentOutOfRangeException>(() => { engine.ThresholdMs = invalidThreshold; });
    }
}
