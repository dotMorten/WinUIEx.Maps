using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Foundation;

namespace WinUIEx.Maps.Tests.Tests;

[TestClass]
public sealed class TouchPitchStateTests
{
    [TestMethod]
    public void AmbiguousCentroidDriftMustNotActivatePitch()
    {
        TouchPitchState state = Pair();
        Assert.IsTrue(Step(state, -9, out double pitch));
        Assert.AreEqual(0, pitch, "Centroid motion alone is not parallel-contact evidence.");
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(1)]
    public void OneFingerLedPinchWithCentroidDriftChoosesNormal(int direction)
    {
        TouchPitchState state = Pair();
        // One finger moves vertically: midpoint crosses the old 8 DIP threshold
        // while separation changes by less than 6 DIPs.
        MovePair(state, new(0, -20), new(120, 0), 1);
        Assert.IsTrue(Step(state, -10, out double pitch));
        Assert.AreEqual(0, pitch);
        MovePair(state, new(-direction * 20, -35), new(120 + direction * 10, -12), 2);
        Assert.IsFalse(state.TryGetPitchDelta(new(3, -13.5), 1.2, 8, 2, false,
            out Point translation, out double scale, out pitch));
        Assert.AreEqual(new Point(3, -23.5), translation);
        Assert.AreEqual(1.2, scale);
        Assert.AreEqual(0, pitch);
        MovePair(state, new(0, -80), new(120, -80), 3);
        Assert.IsFalse(Step(state, -40, out pitch));
        Assert.AreEqual(0, pitch, "Normal mode cannot switch to pitch.");
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(1)]
    public void ParallelJitterRequiresBothContactsAndHasNoActivationJump(int direction)
    {
        TouchPitchState state = Pair();
        state.Move(1, new(1, direction * 20), 1);
        Assert.IsTrue(Step(state, direction * 10, out double pitch));
        Assert.AreEqual(0, pitch, "Never classify a half-updated frame.");
        state.Move(2, new(121, direction * 19), 2);
        Assert.IsTrue(Step(state, direction * 9.5, out pitch));
        Assert.AreEqual(0, pitch, "Different frames are not a pair.");
        state.Move(1, new(1, direction * 20), 2);
        Assert.IsTrue(Step(state, 0, out pitch));
        Assert.AreEqual(0, pitch, "Pitch activation must not replay buffered motion.");
        Assert.IsTrue(Step(state, direction * 8, out pitch));
        Assert.AreEqual(-direction * 2, pitch);
        MovePair(state, new(-40, 0), new(160, 0), 3);
        Assert.IsTrue(Step(state, direction * 4, out pitch));
        Assert.AreEqual(-direction, pitch, "Pitch remains locked despite later separation.");
    }

    [TestMethod]
    [DataRow(16, 0, true)]
    [DataRow(17, 0, true)]
    [DataRow(20, 6, true)]
    [DataRow(20, 6.01, false)]
    public void SeparationHasPriorityAtBoundaries(double vertical, double separation, bool consumed)
    {
        TouchPitchState state = Pair();
        MovePair(state, new(-separation / 2, -vertical),
            new(120 + separation / 2, -vertical), 1);
        Assert.AreEqual(consumed, Step(state, -vertical, out double pitch));
        Assert.AreEqual(0, pitch);
        bool next = Step(state, -4, out pitch);
        Assert.AreEqual(consumed, next);
        Assert.AreEqual(consumed && vertical > 16 && separation <= vertical / 4 ? 1 : 0, pitch);
    }

    [TestMethod]
    public void AmbiguousStartThenSeparationReplaysAllNormalMotion()
    {
        TouchPitchState state = Pair();
        MovePair(state, new(0, -10), new(120, -9), 1);
        Assert.IsTrue(state.TryGetPitchDelta(new(1, -9.5), 1.01, 0, 4, false,
            out _, out _, out double pitch));
        Assert.AreEqual(0, pitch);
        MovePair(state, new(-10, -20), new(130, -20), 2);
        Assert.IsFalse(state.TryGetPitchDelta(new(2, -10.5), 1.02, 20, 7, false,
            out Point translation, out double scale, out pitch));
        Assert.AreEqual(new Point(3, -20), translation);
        Assert.AreEqual(1.01 * 1.02, scale, 1e-10);
        var rotation = new TouchRotationState();
        Assert.AreEqual(1, rotation.GetRotationDelta(11), "Cumulative rotation replays excess over 10 degrees.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ContactRetirementAndResetPreserveCaptureLifecycle(bool canceled)
    {
        TouchPitchState state = Pair();
        MovePair(state, new(0, -20), new(120, -20), 1);
        Assert.IsTrue(Step(state, -20, out _));
        if (canceled) Assert.IsTrue(state.Cancel(1));
        else state.Release(1);
        Assert.IsTrue(Step(state, -20, out double pitch));
        Assert.AreEqual(0, pitch);
        state.Release(2);
        Assert.IsFalse(state.Cancel(2), "CaptureLost after release is harmless.");
        state.Reset();
        state.Press(8, false);
        state.Press(3);
        Assert.IsFalse(Step(state, 20, out _), "Uncaptured contacts cannot become ghosts.");
        state.Press(4, position: new(120, 0));
        state.Reset();
        state.Move(3, new(0, -20), 2);
        state.Move(4, new(120, -20), 2);
        Assert.IsTrue(Step(state, -20, out pitch));
        Assert.AreEqual(0, pitch);
        Assert.IsTrue(Step(state, -8, out pitch));
        Assert.AreEqual(2, pitch);
        state.Clear();
        Assert.IsFalse(Step(state, 10, out _));
    }

    [TestMethod]
    public void UndecidedReleaseFlushesBufferAndSingleFingerInertiaStillPans()
    {
        TouchPitchState state = Pair();
        Assert.IsTrue(Step(state, -7, out _));
        state.Release(2);
        Assert.IsFalse(state.TryGetPitchDelta(new(0, -3), 1, 0, 0, false,
            out Point translation, out _, out _));
        Assert.AreEqual(new Point(0, -10), translation);
        Assert.IsFalse(state.TryGetPitchDelta(new(0, -2), 1, 0, 0, true,
            out translation, out _, out _));
        Assert.AreEqual(new Point(0, -2), translation);
    }

    private static TouchPitchState Pair()
    {
        var state = new TouchPitchState();
        state.Press(1);
        state.Press(2, position: new(120, 0));
        state.Reset();
        return state;
    }

    [TestMethod]
    public void RejectedResetRetainsContactsButDiscardsClassificationAndFrameHistory()
    {
        TouchPitchState state = Pair();
        MovePair(state, new(-10, 0), new(130, 0), 100);
        Assert.IsFalse(Step(state, 0, out _));
        state.Reset();
        MovePair(state, new(-10, -20), new(130, -20), 1);
        Assert.IsTrue(Step(state, -20, out double pitch));
        Assert.AreEqual(0, pitch);
        Assert.IsTrue(Step(state, -4, out pitch));
        Assert.AreEqual(1, pitch);
    }

    [TestMethod]
    public void ManipulationStartingBetweenContactUpdatesKeepsCoherentBaseline()
    {
        TouchPitchState state = Pair();
        state.Move(1, new(0, -20), 10);
        state.Reset(preserveContactEvidence: true);
        state.Move(2, new(120, -20), 10);
        Assert.IsTrue(Step(state, -20, out double pitch));
        Assert.AreEqual(0, pitch);
        Assert.IsTrue(Step(state, -4, out pitch));
        Assert.AreEqual(1, pitch);
    }

    [TestMethod]
    public void CompletionBeforeFinalReleaseDoesNotLoseNextPairBaseline()
    {
        TouchPitchState state = Pair();
        state.Release(1);
        state.Reset(); // XAML may complete before dispatching the second release.
        state.Release(2);
        state.Press(3);
        state.Press(4, position: new(120, 0));
        state.Move(3, new(0, -20), 1);
        state.Reset(preserveContactEvidence: true);
        state.Move(4, new(120, -20), 1);
        Assert.IsTrue(Step(state, -20, out _));
        Assert.IsTrue(Step(state, -4, out double pitch));
        Assert.AreEqual(1, pitch);
    }

    [TestMethod]
    public void CoalescedSamplesAfterHistoryEvictionStillPairInOrder()
    {
        TouchPitchState state = Pair();
        // Fill and recycle the bounded history before a coalesced delivery.
        for (uint frame = 1; frame <= 20; frame++)
        {
            MovePair(state, new(0, -2), new(120, -2), frame);
            Assert.IsTrue(Step(state, 0, out _));
        }
        state.Move(1, new(0, -18), 21);
        state.Move(1, new(0, -20), 22);
        state.Move(2, new(120, -18), 21);
        state.Move(2, new(120, -20), 22);
        // Late historical points must not overwrite the latest complete pair.
        state.Move(1, new(0, -2), 20);
        state.Move(2, new(120, -2), 20);
        Assert.IsTrue(Step(state, -20, out _));
        Assert.IsTrue(Step(state, -4, out double pitch));
        Assert.AreEqual(1, pitch);
    }

    [TestMethod]
    [DataRow(9.99, true)]
    [DataRow(10.01, false)]
    public void RawTwistUsesExistingTenDegreeBoundary(double angle, bool undecided)
    {
        TouchPitchState state = Pair();
        double radians = angle * Math.PI / 180;
        MovePair(state, new(60 - 60 * Math.Cos(radians), -60 * Math.Sin(radians)),
            new(60 + 60 * Math.Cos(radians), 60 * Math.Sin(radians)), 1);
        Assert.AreEqual(undecided, Step(state, 0, out double pitch));
        Assert.AreEqual(0, pitch);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(4)]
    [DataRow(20)]
    public void NetEvidenceDoesNotDependOnSamplingCount(int samples)
    {
        TouchPitchState state = Pair();
        for (uint i = 1; i <= samples; i++)
        {
            double y = -20d * i / samples;
            MovePair(state, new(0, y), new(120, 0), i);
            Assert.IsTrue(Step(state, -10d / samples, out double pitch));
            Assert.AreEqual(0, pitch);
        }
        MovePair(state, new(-10, -30), new(130, -10), (uint)samples + 1);
        Assert.IsFalse(Step(state, -10, out _));
    }

    private static void MovePair(TouchPitchState state, Point first, Point second, uint frame)
    {
        state.Move(1, first, frame);
        state.Move(2, second, frame);
    }

    private static bool Step(TouchPitchState state, double vertical, out double pitch) =>
        state.TryGetPitchDelta(new Point(0, vertical), 1, 0, 0, false,
            out _, out _, out pitch);
}
