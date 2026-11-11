using System;
using System.Collections.Generic;
using System.Reflection;
using BaobaoDesktop;

public static class InteractionPacingRegressionTests
{
    private sealed class Waveform
    {
        public readonly List<int> Starts = new List<int>();
        public readonly List<int> Holds = new List<int>();
    }
    private static int failures;
    private static Waveform waveform;

    public static int Main()
    {
        waveform = MeasurePlay();
        Console.WriteLine("OBSERVE pawStarts=" + waveform.Starts.Count + " minVisibleMs=" + Minimum(waveform.Holds) + " minStartGapMs=" + MinimumGaps(waveform.Starts));
        Run("a reach remains readable for at least half a second", ReadableReach);
        Run("reaches leave observation time between starts", RestBetweenReaches);
        Run("small center jitter does not flip the head repeatedly", CenterJitter);
        Run("large alternating targets respect a short orientation hold", OrientationHold);
        Run("a stable distant target still receives timely attention", ResponsiveFarTarget);
        Run("petting has a continuous entry and a half-second exit", PetEnvelope);
        Run("pet sway is bounded and gently periodic", PetSway);
        Run("pet parameters freeze on pause and cancel without success", PetPauseAndCancel);
        Run("a raised paw lands before switching sides", PawSideContinuity);
        Run("a six-second play session returns before its one completion", SixSecondCallerSession);
        Console.WriteLine(failures == 0 ? "PASS: 10 pacing regressions" : "FAIL: " + failures + " pacing regressions");
        return failures == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static bool Raised(int frame) { return frame == 25 || frame == 27; }
    private static int Direction(int frame) { return frame == 24 || frame == 25 ? -1 : frame == 26 || frame == 27 ? 1 : 0; }
    private static int Minimum(List<int> values) { int result = Int32.MaxValue; foreach (int value in values) result = Math.Min(result, value); return result; }
    private static int MinimumGaps(List<int> values)
    {
        int result = Int32.MaxValue;
        for (int i = 1; i < values.Count; i++) result = Math.Min(result, values[i] - values[i - 1]);
        return result;
    }
    private static Waveform MeasurePlay()
    {
        InteractionController cat = new InteractionController(42); cat.BeginPlay(); cat.SetPlayTarget(.2);
        Waveform result = new Waveform(); bool wasRaised = false; int start = 0;
        for (int time = 10; time <= 12000; time += 10)
        {
            cat.Tick(10); bool raised = Raised(cat.FrameIndex);
            if (raised && !wasRaised) { start = time; result.Starts.Add(time); }
            if (!raised && wasRaised) result.Holds.Add(time - start);
            wasRaised = raised;
        }
        Assert(cat.IsBusy && cat.CurrentKind == InteractionKind.Play, "Play ended without an explicit End call.");
        return result;
    }
    private static void ReadableReach()
    {
        Assert(waveform.Holds.Count >= 3, "Not enough complete reaches observed.");
        foreach (int hold in waveform.Holds) Assert(hold >= 500 && hold <= 900, "Observed reach hold=" + hold + "ms.");
    }
    private static void RestBetweenReaches()
    {
        Assert(waveform.Starts.Count >= 3 && waveform.Starts.Count <= 8, "Twelve-second scene has an excessively dense or sparse reach cadence: " + waveform.Starts.Count);
        Assert(MinimumGaps(waveform.Starts) >= 1600, "Observed start gap=" + MinimumGaps(waveform.Starts) + "ms.");
    }
    private static void CenterJitter()
    {
        InteractionController cat = new InteractionController(); cat.BeginPlay(); cat.SetPlayTarget(.2); cat.Tick(400);
        int previous = Direction(cat.FrameIndex), changes = 0;
        for (int i = 0; i < 50; i++)
        {
            cat.SetPlayTarget(i % 2 == 0 ? .49 : .51); cat.Tick(20);
            int next = Direction(cat.FrameIndex); if (next != previous) changes++; previous = next;
        }
        Console.WriteLine("OBSERVE centerJitterHeadChangesPerSecond=" + changes);
        Assert(changes <= 1, "Center jitter changed orientation " + changes + " times in one second.");
    }
    private static void OrientationHold()
    {
        InteractionController cat = new InteractionController(); cat.BeginPlay(); cat.SetPlayTarget(.1);
        int previous = Direction(cat.FrameIndex), lastChange = 0, smallestGap = Int32.MaxValue, changes = 0;
        for (int time = 20; time <= 1000; time += 20)
        {
            cat.SetPlayTarget(time % 40 == 0 ? .1 : .9); cat.Tick(20);
            int next = Direction(cat.FrameIndex);
            if (next != previous) { smallestGap = Math.Min(smallestGap, time - lastChange); lastChange = time; changes++; }
            previous = next;
        }
        Console.WriteLine("OBSERVE wideJitterHeadChanges=" + changes + " minOrientationHoldMs=" + smallestGap);
        Assert(changes > 0 && smallestGap >= 200, "Alternating targets changed the head after only " + smallestGap + "ms.");
    }
    private static void ResponsiveFarTarget()
    {
        InteractionController cat = new InteractionController(); cat.BeginPlay(); cat.SetPlayTarget(.1);
        for (int time = 0; Direction(cat.FrameIndex) != -1 && time < 350; time += 10) cat.Tick(10);
        Assert(Direction(cat.FrameIndex) == -1, "Stable left target was ignored.");
        cat.SetPlayTarget(.9);
        for (int time = 0; Direction(cat.FrameIndex) != 1 && time < 350; time += 10) cat.Tick(10);
        Assert(Direction(cat.FrameIndex) == 1, "Orientation filtering made stable attention too sluggish.");
    }

    private static double Value(InteractionController cat, string name)
    {
        PropertyInfo property = typeof(InteractionController).GetProperty(name);
        Assert(property != null, "Missing continuous parameter " + name + ".");
        return (double)property.GetValue(cat, null);
    }
    private static void PetEnvelope()
    {
        InteractionController cat = new InteractionController(); int completed = 0; cat.Completed += delegate { completed++; }; cat.BeginPetting();
        Assert(Value(cat, "PetBlend") == 0 && Value(cat, "PetSway") == 0, "Pet entry began with a pose jump.");
        double priorBlend = 0, priorSway = 0;
        for (int time = 10; time <= 400; time += 10)
        {
            cat.Tick(10); double blend = Value(cat, "PetBlend"), sway = Value(cat, "PetSway");
            Assert(blend >= priorBlend && blend <= 1 && Math.Abs(blend - priorBlend) < .09 && Math.Abs(sway - priorSway) < .09, "Pet entry is discontinuous.");
            priorBlend = blend; priorSway = sway;
        }
        Assert(Math.Abs(priorBlend - 1) < 1e-10, "Pet did not settle into the continuous pose.");
        cat.EndPetting(true); Assert(Value(cat, "PetBlend") == priorBlend && completed == 0, "Ending petting snapped or completed immediately.");
        cat.Tick(250); Assert(Math.Abs(Value(cat, "PetBlend") - .5) < .02 && cat.IsBusy && completed == 0, "Return did not ease halfway through its half-second exit.");
        cat.Tick(250); Assert(!cat.IsBusy && Value(cat, "PetBlend") == 0 && Value(cat, "PetSway") == 0 && completed == 1, "Pet parameters/feedback did not finish together at idle.");
    }
    private static void PetSway()
    {
        InteractionController cat = new InteractionController(); cat.BeginPetting(); cat.Tick(400);
        double atStart = Value(cat, "PetSway"), maximum = -1, minimum = 1;
        for (int time = 10; time <= 3200; time += 10)
        {
            cat.Tick(10); double sway = Value(cat, "PetSway"), blend = Value(cat, "PetBlend");
            Assert(Math.Abs(sway) <= blend + 1e-10, "Sway exceeded its entry/exit envelope."); maximum = Math.Max(maximum, sway); minimum = Math.Min(minimum, sway);
        }
        Assert(maximum > .5 && minimum < -.5 && Math.Abs(Value(cat, "PetSway") - atStart) < 1e-8, "Pet sway is not a gentle reversible cycle.");
    }
    private static void PetPauseAndCancel()
    {
        InteractionController cat = new InteractionController(); int completed = 0; cat.Completed += delegate { completed++; }; cat.BeginPetting(); cat.Tick(600);
        double blend = Value(cat, "PetBlend"), sway = Value(cat, "PetSway"); cat.Paused = true; cat.Tick(10000);
        Assert(Value(cat, "PetBlend") == blend && Value(cat, "PetSway") == sway, "Pause advanced continuous pet motion.");
        cat.EndPetting(false); cat.Tick(10000); Assert(Value(cat, "PetBlend") == blend && Value(cat, "PetSway") == sway && completed == 0, "Paused cancellation advanced its return.");
        cat.Paused = false; cat.Tick(500); Assert(!cat.IsBusy && Value(cat, "PetBlend") == 0 && Value(cat, "PetSway") == 0 && completed == 0, "Cancellation emitted success or left a pet offset.");
    }
    private static void PawSideContinuity()
    {
        InteractionController cat = new InteractionController(); cat.BeginPlay(); cat.SetPlayTarget(.1);
        for (int time = 0; !Raised(cat.FrameIndex) && time < 5000; time += 10) cat.Tick(10);
        Assert(cat.FrameIndex == 25, "No left reach observed."); cat.SetPlayTarget(.9);
        for (int time = 0; Raised(cat.FrameIndex) && time < 1500; time += 10) { Assert(cat.FrameIndex == 25, "Raised paw switched sides in midair."); cat.Tick(10); }
        Assert(!Raised(cat.FrameIndex), "Paw never landed.");
    }
    private static void SixSecondCallerSession()
    {
        InteractionController cat = new InteractionController(); int completed = 0; cat.Completed += delegate { completed++; }; cat.BeginPlay(); cat.Tick(6000);
        Assert(cat.IsBusy && completed == 0, "Controller auto-completed before its caller ended the session."); cat.EndPlay(true);
        for (int time = 0; cat.IsBusy && time < 1200; time += 10) cat.Tick(10);
        Assert(!cat.IsBusy && cat.FrameIndex == 0 && completed == 1, "Completed play did not return and emit once.");
    }
}
