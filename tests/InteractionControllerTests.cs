using System;
using System.Collections.Generic;

namespace BaobaoDesktop
{
    public static class InteractionControllerTests
    {
        private static int failures;
        public static int Main()
        {
            Run("idle blink keeps the approved frames", IdleBlink);
            Run("eating emits one completion only after idle", EatCompletion);
            Run("drinking covers entry, lapping and return", DrinkCompletion);
            Run("busy starts are rejected without resetting", BusyStarts);
            Run("shallow eating cancellation never dives", CancelEat);
            Run("shallow drinking cancellation never dives", CancelDrink);
            Run("petting remains active until explicitly ended", PetSession);
            Run("cancelled petting never completes", PetCancellation);
            Run("play remains active until explicitly ended", PlaySession);
            Run("cancelled play returns without feedback", PlayCancellation);
            Run("pause freezes timelines and completion", Pause);
            Run("feed is deterministic and busy calls consume no randomness", FeedRandomness);
            Run("play target clamps and a raised paw lands before side switching", PlayDirection);
            Run("play paw starts remain sparse and readable", PlayReachInterval);
            Run("large finite time stays legal for every mode", LargeTime);
            Run("invalid time does not change partial progress", InvalidTime);
            Run("completion reentrancy cannot replay stale time", ReentrantCompletion);
            Run("explicit cancellation revokes a pending success", CancelPendingSuccess);
            Run("reset clears motion without feedback and restores seed", Reset);
            Run("scratching covers its real toy poses and completes once", ScratchCompletion);
            Run("climbing pauses on top then returns and completes once", ClimbCompletion);
            Run("toy cancellation uses its own safe return poses", ToyCancellation);
            Run("canonical action IDs map without bypassing busy guards", ActionIds);
            Console.WriteLine(failures == 0 ? "PASS: 23 tests" : "FAIL: " + failures + " tests");
            return failures == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            try { test(); Console.WriteLine("PASS " + name); }
            catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
        }
        private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
        private static void Same(InteractionController a, InteractionController b)
        {
            Assert(a.FrameIndex == b.FrameIndex && a.CurrentKind == b.CurrentKind && a.IsBusy == b.IsBusy, "Timelines differ.");
        }
        private static HashSet<int> Finish(InteractionController cat)
        {
            HashSet<int> seen = new HashSet<int>();
            for (int i = 0; i < 1000 && cat.IsBusy; i++) { seen.Add(cat.FrameIndex); cat.Tick(10); }
            seen.Add(cat.FrameIndex);
            Assert(!cat.IsBusy && cat.CurrentKind == InteractionKind.Idle && cat.FrameIndex == 0, "Action did not reach neutral idle.");
            return seen;
        }
        private static void WaitForRaisedPaw(InteractionController cat)
        {
            for (int time = 0; cat.FrameIndex != 25 && cat.FrameIndex != 27 && time < 4000; time += 10) cat.Tick(10);
            Assert(cat.FrameIndex == 25 || cat.FrameIndex == 27, "No reach was observed within the play cadence.");
        }
        private static void WaitForFacing(InteractionController cat, double target, int direction)
        {
            cat.SetPlayTarget(target);
            for (int time = 0; cat.FrameIndex != direction && time < 350; time += 10) cat.Tick(10);
            Assert(cat.FrameIndex == direction, "Stable target did not receive attention after the short orientation hold.");
        }

        private static void IdleBlink()
        {
            InteractionController cat = new InteractionController();
            cat.Tick(3700); Assert(cat.FrameIndex == 1 && !cat.IsBusy, "Half blink missing.");
            cat.Tick(75); Assert(cat.FrameIndex == 2, "Closed eyes missing.");
            cat.Tick(110); Assert(cat.FrameIndex == 3, "Reopening missing.");
            cat.Tick(85); Assert(cat.FrameIndex == 0, "Blink did not return.");
            Assert(!cat.TryStart(InteractionKind.Idle) && !cat.TryStart((InteractionKind)99), "Invalid starts were accepted.");
        }

        private static void EatCompletion()
        {
            InteractionController cat = new InteractionController(); int count = 0;
            cat.Completed += delegate(InteractionKind kind) { Assert(kind == InteractionKind.Eat && !cat.IsBusy && cat.CurrentKind == InteractionKind.Idle && cat.FrameIndex == 0, "Feedback preceded idle."); count++; };
            Assert(cat.TryStart(InteractionKind.Eat), "Eat refused.");
            HashSet<int> seen = Finish(cat);
            for (int frame = 4; frame <= 11; frame++) Assert(seen.Contains(frame), "Eat omitted frame " + frame + ".");
            cat.Tick(100000); Assert(count == 1, "Eat completed more than once.");
        }

        private static void DrinkCompletion()
        {
            InteractionController cat = new InteractionController(); int count = 0;
            cat.Completed += delegate(InteractionKind kind) { Assert(kind == InteractionKind.Drink && !cat.IsBusy, "Wrong drink outcome."); count++; };
            cat.TryStart(InteractionKind.Drink); HashSet<int> seen = Finish(cat);
            for (int frame = 12; frame <= 19; frame++) Assert(seen.Contains(frame), "Drink omitted frame " + frame + ".");
            cat.Tick(5000); Assert(count == 1, "Drink did not emit exactly one completion.");
        }

        private static void BusyStarts()
        {
            InteractionController cat = new InteractionController(9), baseline = new InteractionController(9);
            cat.TryStart(InteractionKind.Eat); baseline.TryStart(InteractionKind.Eat); cat.Tick(620); baseline.Tick(620);
            Assert(!cat.TryStart(InteractionKind.Eat) && !cat.TryStart(InteractionKind.Drink) && !cat.BeginPetting() && !cat.BeginPlay() && !cat.TryStart(InteractionKind.Scratch) && !cat.TryStart(InteractionKind.Climb) && !cat.Feed(), "Busy accepted another action.");
            for (int i = 0; i < 400; i++) { cat.Tick(13); baseline.Tick(13); Same(cat, baseline); }
        }

        private static void CancelEat()
        {
            InteractionController cat = new InteractionController(); int count = 0; cat.Completed += delegate { count++; };
            cat.TryStart(InteractionKind.Eat); cat.Tick(600); Assert(cat.FrameIndex == 5, "Wrong shallow eat fixture.");
            cat.ReturnToIdle(); HashSet<int> seen = Finish(cat);
            Assert(!seen.Contains(6) && !seen.Contains(7) && !seen.Contains(8) && !seen.Contains(9) && !seen.Contains(10), "Eat cancellation dipped deeper.");
            Assert(count == 0, "Cancellation emitted completion.");
        }

        private static void CancelDrink()
        {
            InteractionController cat = new InteractionController(); int count = 0; cat.Completed += delegate { count++; };
            cat.TryStart(InteractionKind.Drink); cat.Tick(600); Assert(cat.FrameIndex == 13, "Wrong shallow drink fixture.");
            cat.ReturnToIdle(); HashSet<int> seen = Finish(cat);
            Assert(!seen.Contains(14) && !seen.Contains(15) && !seen.Contains(16) && !seen.Contains(17) && !seen.Contains(18), "Drink cancellation dipped deeper.");
            Assert(count == 0, "Drink cancellation emitted completion.");
        }

        private static void PetSession()
        {
            InteractionController cat = new InteractionController(); int count = 0;
            cat.Completed += delegate(InteractionKind kind) { Assert(kind == InteractionKind.Pet && !cat.IsBusy, "Pet feedback was early."); count++; };
            cat.BeginPetting(); cat.Tick(1e12);
            Assert(cat.IsBusy && cat.CurrentKind == InteractionKind.Pet && (cat.FrameIndex == 21 || cat.FrameIndex == 22) && count == 0, "Long pet session ended itself.");
            Assert(cat.PetBlend == 1 && !Double.IsNaN(cat.PetSway) && Math.Abs(cat.PetSway) <= 1, "Long pet session lost its continuous pose bounds.");
            cat.EndPetting(true); Assert(cat.IsBusy && count == 0, "Pet end emitted before return.");
            HashSet<int> seen = Finish(cat); Assert(seen.Contains(23) && count == 1 && cat.PetBlend == 0 && cat.PetSway == 0, "Pet return/feedback missing or left an offset.");
            cat.EndPetting(true); cat.Tick(20000); Assert(count == 1, "Pet completed twice.");
        }

        private static void PetCancellation()
        {
            InteractionController cat = new InteractionController(); int count = 0; cat.Completed += delegate { count++; };
            cat.BeginPetting(); cat.Tick(4000); cat.EndPetting(false); Finish(cat);
            Assert(count == 0, "Cancelled pet emitted success.");
        }

        private static void PlaySession()
        {
            InteractionController cat = new InteractionController(); int count = 0;
            cat.Completed += delegate(InteractionKind kind) { Assert(kind == InteractionKind.Play && !cat.IsBusy, "Play feedback was early."); count++; };
            cat.BeginPlay(); cat.Tick(1e12);
            Assert(cat.IsBusy && cat.CurrentKind == InteractionKind.Play && count == 0, "Long play session ended itself.");
            cat.ReturnToIdle(); Finish(cat); cat.BeginPlay(); cat.SetPlayTarget(.1); WaitForRaisedPaw(cat); Assert(cat.FrameIndex == 25, "Play did not extend its left paw.");
            cat.EndPlay(true); Assert(count == 0 && cat.IsBusy, "Play emitted before paw landed.");
            HashSet<int> seen = Finish(cat); Assert(seen.Contains(24) && count == 1, "Play did not land paw or emit completion.");
        }

        private static void PlayCancellation()
        {
            InteractionController cat = new InteractionController(); int count = 0; cat.Completed += delegate { count++; };
            cat.BeginPlay(); cat.Tick(650); cat.EndPlay(false); Finish(cat);
            Assert(count == 0, "Cancelled play emitted success.");
        }

        private static void Pause()
        {
            InteractionController cat = new InteractionController(), baseline = new InteractionController();
            cat.TryStart(InteractionKind.Drink); baseline.TryStart(InteractionKind.Drink); cat.Tick(600); baseline.Tick(600);
            cat.Paused = true; cat.Tick(Double.MaxValue); Same(cat, baseline); cat.Paused = false;
            for (int i = 0; i < 400; i++) { cat.Tick(13); baseline.Tick(13); Same(cat, baseline); }
            int completed = 0; cat.Completed += delegate { completed++; }; cat.BeginPetting(); cat.Tick(700); cat.Paused = true; cat.EndPetting(true);
            int frame = cat.FrameIndex; cat.Tick(10000); Assert(cat.FrameIndex == frame && completed == 0 && cat.IsBusy, "Paused return advanced.");
            cat.Paused = false; Finish(cat); Assert(completed == 1, "Resumed return did not complete.");
            cat.BeginPlay(); cat.SetPlayTarget(0); cat.Paused = true; frame = cat.FrameIndex; cat.SetPlayTarget(1); cat.Tick(20000); Assert(cat.FrameIndex == frame, "Paused play followed input.");
        }

        private static void FeedRandomness()
        {
            InteractionController cat = new InteractionController(42), baseline = new InteractionController(42); int eats = 0, drinks = 0;
            for (int i = 0; i < 200; i++)
            {
                Assert(cat.Feed() && baseline.Feed(), "Idle feed refused."); Same(cat, baseline);
                if (cat.CurrentKind == InteractionKind.Eat) eats++; else if (cat.CurrentKind == InteractionKind.Drink) drinks++; else throw new Exception("Feed selected a non-food interaction.");
                cat.Tick(100); baseline.Tick(100);
                for (int j = 0; j < 10; j++) Assert(!cat.Feed(), "Busy feed was accepted.");
                cat.Tick(5000); baseline.Tick(5000); Same(cat, baseline);
            }
            Assert(eats >= 80 && eats <= 120 && drinks == 200 - eats, "Seeded feed did not give both Eat and Drink frequent visibility.");
        }

        private static void PlayDirection()
        {
            InteractionController cat = new InteractionController(); cat.BeginPlay(); cat.SetPlayTarget(-4); Assert(cat.FrameIndex == 24, "Negative target did not clamp left.");
            WaitForRaisedPaw(cat); Assert(cat.FrameIndex == 25, "Left reach missing."); cat.SetPlayTarget(8); Assert(cat.FrameIndex == 25, "Raised left paw switched abruptly.");
            for (int time = 0; (cat.FrameIndex == 25 || cat.FrameIndex == 27) && time < 1500; time += 10) cat.Tick(10);
            Assert(cat.FrameIndex == 26, "Paw did not land facing right.");
            cat.SetPlayTarget(Double.NaN); Assert(cat.FrameIndex == 26, "NaN changed tracking.");
            WaitForFacing(cat, Double.NegativeInfinity, 24);
            WaitForFacing(cat, Double.PositiveInfinity, 26);
            WaitForRaisedPaw(cat); Assert(cat.FrameIndex == 27, "Right reach missing at next interval.");
        }

        private static void PlayReachInterval()
        {
            InteractionController cat = new InteractionController(); cat.BeginPlay(); cat.SetPlayTarget(0);
            bool previousRaised = false; int lastStart = -1, starts = 0;
            for (int time = 10; time <= 10000; time += 10)
            {
                cat.Tick(10); bool raised = cat.FrameIndex == 25 || cat.FrameIndex == 27;
                if (raised && !previousRaised) { if (lastStart >= 0) Assert(time - lastStart >= 1600, "Paw starts were too close to read."); lastStart = time; starts++; }
                previousRaised = raised;
            }
            Assert(starts >= 3 && starts <= 6 && cat.IsBusy, "Play did not keep a readable response cadence.");
        }

        private static void LargeTime()
        {
            foreach (InteractionKind kind in new InteractionKind[] { InteractionKind.Eat, InteractionKind.Drink, InteractionKind.Pet, InteractionKind.Play, InteractionKind.Scratch, InteractionKind.Climb })
            {
                InteractionController cat = new InteractionController(); int count = 0; cat.Completed += delegate { count++; }; cat.TryStart(kind); cat.Tick(Double.MaxValue);
                Assert(cat.FrameIndex >= 0 && cat.FrameIndex < 44, "Large time produced an invalid frame.");
                Assert(!Double.IsNaN(cat.PetBlend) && !Double.IsNaN(cat.PetSway) && cat.PetBlend >= 0 && cat.PetBlend <= 1 && Math.Abs(cat.PetSway) <= cat.PetBlend, "Large time produced invalid continuous pet parameters.");
                if (kind != InteractionKind.Pet) Assert(cat.PetBlend == 0 && cat.PetSway == 0, "A non-pet action inherited a pet offset.");
                if (kind == InteractionKind.Eat || kind == InteractionKind.Drink || kind == InteractionKind.Scratch || kind == InteractionKind.Climb) Assert(!cat.IsBusy && count == 1, "Finite action did not finish exactly once.");
                else Assert(cat.IsBusy && cat.CurrentKind == kind && count == 0, "Large time ended a held session.");
            }
        }

        private static void InvalidTime()
        {
            InteractionController cat = new InteractionController(), baseline = new InteractionController();
            cat.TryStart(InteractionKind.Drink); baseline.TryStart(InteractionKind.Drink); cat.Tick(500); baseline.Tick(500);
            foreach (double value in new double[] { -1, -Double.MaxValue, 0, Double.NaN, Double.NegativeInfinity, Double.PositiveInfinity }) cat.Tick(value);
            for (int i = 0; i < 500; i++) { cat.Tick(11); baseline.Tick(11); Same(cat, baseline); }
        }

        private static void ReentrantCompletion()
        {
            InteractionController cat = new InteractionController(); int count = 0;
            cat.Completed += delegate { count++; Assert(!cat.IsBusy && cat.FrameIndex == 0, "Callback did not observe idle."); cat.BeginPlay(); };
            cat.TryStart(InteractionKind.Eat); cat.Tick(Double.MaxValue);
            Assert(count == 1 && cat.CurrentKind == InteractionKind.Play && cat.FrameIndex == 26, "Stale delta replayed or reset the callback's new action.");
        }

        private static void CancelPendingSuccess()
        {
            InteractionController cat = new InteractionController(); int count = 0; cat.Completed += delegate { count++; };
            cat.BeginPetting(); cat.Tick(500); cat.EndPetting(true); cat.Tick(100); cat.ReturnToIdle(); Finish(cat);
            Assert(count == 0, "Explicit cancellation retained pending success.");
            cat.BeginPlay(); cat.Tick(650); cat.EndPlay(true); cat.EndPlay(false); Finish(cat);
            Assert(count == 1, "A duplicate end notification replaced the first committed result.");
        }

        private static void Reset()
        {
            InteractionController cat = new InteractionController(9), fresh = new InteractionController(9); int count = 0; cat.Completed += delegate { count++; };
            cat.Feed(); cat.Tick(500); cat.Paused = true; cat.Reset();
            Assert(!cat.IsBusy && cat.CurrentKind == InteractionKind.Idle && cat.FrameIndex == 0 && !cat.Paused && cat.PetBlend == 0 && cat.PetSway == 0 && count == 0, "Reset emitted feedback or retained state.");
            cat.Feed(); fresh.Feed(); Assert(cat.CurrentKind == fresh.CurrentKind, "Reset did not restore its constructor seed.");
        }

        private static void ScratchCompletion()
        {
            InteractionController cat = new InteractionController(); int count = 0;
            cat.Completed += delegate(InteractionKind kind) { Assert(kind == InteractionKind.Scratch && !cat.IsBusy && cat.FrameIndex == 0, "Wrong scratch completion state."); count++; };
            Assert(cat.TryStart(InteractionKind.Scratch), "Scratcher refused.");
            HashSet<int> seen = Finish(cat);
            for (int frame = 28; frame <= 35; frame++) Assert(seen.Contains(frame), "Scratch omitted frame " + frame + ".");
            cat.Tick(20000); Assert(count == 1, "Scratch completed more than once.");
        }

        private static void ClimbCompletion()
        {
            InteractionController cat = new InteractionController(); int count = 0;
            cat.Completed += delegate(InteractionKind kind) { Assert(kind == InteractionKind.Climb && !cat.IsBusy && cat.FrameIndex == 0, "Wrong climb completion state."); count++; };
            cat.TryStart(InteractionKind.Climb); HashSet<int> seen = Finish(cat);
            for (int frame = 36; frame <= 43; frame++) Assert(seen.Contains(frame), "Climb omitted frame " + frame + ".");
            Assert(count == 1, "Climb did not complete exactly once.");
            cat.TryStart(InteractionKind.Climb); cat.Tick(1500); Assert(cat.FrameIndex == 40, "Climb did not reach top.");
            cat.Tick(1000); Assert(cat.FrameIndex == 40, "Top perch did not hold before descent.");
            cat.Tick(50); Assert(cat.FrameIndex == 41, "Climb did not begin descent after its hold.");
            cat.ReturnToIdle(); Finish(cat); Assert(count == 1, "Cancelled second climb emitted success.");
        }

        private static void ToyCancellation()
        {
            InteractionController cat = new InteractionController(); int count = 0; cat.Completed += delegate { count++; };
            cat.TryStart(InteractionKind.Scratch); cat.Tick(800); Assert(cat.FrameIndex == 31, "Wrong scratching fixture.");
            Assert(!cat.TryStartAction("toy.cat_tree") && !cat.Feed(), "Busy toy accepted another action.");
            cat.ReturnToIdle(); HashSet<int> scratch = Finish(cat); Assert(scratch.Contains(35), "Scratch did not sit back.");
            foreach (int frame in scratch) Assert(frame == 0 || (frame >= 28 && frame <= 35), "Scratch used an unrelated return index.");
            cat.TryStart(InteractionKind.Climb); cat.Tick(1700); Assert(cat.FrameIndex == 40, "Wrong top-perch fixture.");
            cat.ReturnToIdle(); HashSet<int> climb = Finish(cat); Assert(climb.Contains(41) && climb.Contains(42) && climb.Contains(43), "Top cancellation did not descend safely.");
            foreach (int frame in climb) Assert(frame == 0 || (frame >= 36 && frame <= 43), "Climb used an unrelated return index.");
            Assert(count == 0, "A cancelled toy emitted completion.");
        }

        private static void ActionIds()
        {
            string[] ids = new string[] { "eat", "drink", "pet", "play", "toy.scratcher", "toy.cat_tree" };
            InteractionKind[] kinds = new InteractionKind[] { InteractionKind.Eat, InteractionKind.Drink, InteractionKind.Pet, InteractionKind.Play, InteractionKind.Scratch, InteractionKind.Climb };
            for (int i = 0; i < ids.Length; i++)
            {
                InteractionController cat = new InteractionController();
                Assert(cat.TryStartAction(ids[i]) && cat.CurrentKind == kinds[i], "Wrong action ID mapping: " + ids[i]);
                Assert(!cat.TryStartAction(ids[i]), "Action ID bypassed busy protection.");
                Assert(cat.TryStartAction("idle"), "Idle cancellation ID refused.");
                Finish(cat);
            }
            InteractionController idle = new InteractionController();
            Assert(!idle.TryStartAction(null) && !idle.TryStartAction("toy.unknown") && !idle.IsBusy, "Unknown action ID changed state.");
        }
    }
}
