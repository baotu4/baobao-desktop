using System;
using System.Collections.Generic;

namespace BaobaoDesktop
{
    public static class IdleMotionControllerTests
    {
        private static int failures;

        public static int Main()
        {
            Run("first motion appears in two to four seconds", FirstMotionWindow);
            Run("first visible motion is the tail", FirstMotionIsTail);
            Run("first two events cover tail then a single ear", FirstTwoCoverTailAndEar);
            Run("later tail and ear cadence never repeats a type three times", LaterTypeCadence);
            Run("both single ears and tail occur without overlap", KindsAndSingleEar);
            Run("events are irregular with long quiet rests", IrregularRests);
            Run("pose values remain bounded and finite", Bounds);
            Run("pose changes are continuous at small time steps", Continuity);
            Run("pause freezes motion, clocks and active changes", Pause);
            Run("inactive settles in 220ms and stays neutral", InactiveSettling);
            Run("resume leaves quiet space before another motion", ResumeBuffer);
            Run("early resume completes the smooth settling", EarlyResume);
            Run("invalid time has no side effects", InvalidTime);
            Run("large time gaps rebase without replay or divergence", LargeGap);
            Run("seed and reset reproduce the same motion", DeterminismAndReset);
            Run("normal tick partitioning preserves the motion", Partitioning);
            Console.WriteLine(failures == 0 ? "PASS: 16 tests" : "FAIL: " + failures + " tests");
            return failures == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            try { test(); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static double Magnitude(IdleMotionPose p)
        {
            return Math.Abs(p.LeftEarDegrees) + Math.Abs(p.RightEarDegrees) + Math.Abs(p.TailLift) + Math.Abs(p.TailSway);
        }

        private static void Near(IdleMotionPose a, IdleMotionPose b, double tolerance, string message)
        {
            Assert(Math.Abs(a.LeftEarDegrees - b.LeftEarDegrees) <= tolerance &&
                Math.Abs(a.RightEarDegrees - b.RightEarDegrees) <= tolerance &&
                Math.Abs(a.TailLift - b.TailLift) <= tolerance && Math.Abs(a.TailSway - b.TailSway) <= tolerance, message);
        }

        private static int Kind(IdleMotionPose p)
        {
            if (Math.Abs(p.LeftEarDegrees) > 1e-7) return 1;
            if (Math.Abs(p.RightEarDegrees) > 1e-7) return 2;
            if (Math.Abs(p.TailLift) + Math.Abs(p.TailSway) > 1e-7) return 3;
            return 0;
        }

        private static void FirstMotionWindow()
        {
            for (int seed = 0; seed < 25; seed++)
            {
                IdleMotionController cat = new IdleMotionController(seed);
                int first = 0;
                for (int time = 20; time <= 4040; time += 20)
                {
                    cat.Tick(20, true);
                    if (Kind(cat.Pose) != 0) { first = time; break; }
                }
                Assert(first >= 2000 && first <= 4040, "Startup event was outside its design window for seed " + seed + ".");
            }
        }

        private static void KindsAndSingleEar()
        {
            IdleMotionController cat = new IdleMotionController(42);
            HashSet<int> seen = new HashSet<int>();
            for (int time = 0; time < 300000; time += 20)
            {
                cat.Tick(20, true);
                IdleMotionPose p = cat.Pose;
                bool left = Math.Abs(p.LeftEarDegrees) > 1e-9;
                bool right = Math.Abs(p.RightEarDegrees) > 1e-9;
                bool tail = Math.Abs(p.TailLift) + Math.Abs(p.TailSway) > 1e-9;
                Assert(!(left && right), "Ears moved as a symmetric pair.");
                Assert(!(tail && (left || right)), "Tail and ear events overlapped.");
                if (Kind(p) != 0) seen.Add(Kind(p));
            }
            Assert(seen.Contains(1) && seen.Contains(2) && seen.Contains(3), "Expected both ears and tail across the seeded schedule.");
        }

        private static void FirstMotionIsTail()
        {
            for (int seed = -10; seed <= 10; seed++)
            {
                IdleMotionController cat = new IdleMotionController(seed);
                FindVisibleMotion(cat);
                Assert(cat.Pose.TailLift > 0 && Kind(cat.Pose) == 3, "First gesture was not visible tail detail for seed " + seed + ".");
                Assert(cat.Pose.LeftEarDegrees == 0 && cat.Pose.RightEarDegrees == 0, "Startup tail gesture also moved an ear.");
            }
        }

        private static void FirstTwoCoverTailAndEar()
        {
            for (int seed = 0; seed < 20; seed++)
            {
                List<int> kinds = EventKinds(new IdleMotionController(seed), 2);
                Assert(kinds[0] == 3 && (kinds[1] == 1 || kinds[1] == 2), "First two gestures did not cover tail then one ear.");
            }
        }

        private static void LaterTypeCadence()
        {
            int tails = 0, ears = 0;
            foreach (int seed in new int[] { 7, 31, 42 })
            {
                List<int> kinds = EventKinds(new IdleMotionController(seed), 120);
                bool previousTail = false;
                int streak = 0;
                for (int i = 0; i < kinds.Count; i++)
                {
                    bool tail = kinds[i] == 3;
                    streak = i == 0 || tail != previousTail ? 1 : streak + 1;
                    Assert(streak <= 2, "Tail or ear events repeated three times in succession.");
                    if (i >= 2) { if (tail) tails++; else ears++; }
                    previousTail = tail;
                }
            }
            double ratio = tails / (double)(tails + ears);
            Assert(ratio >= .35 && ratio <= .60, "Seeded later cadence no longer gives frequent visibility to both tail and ears.");
        }

        private static List<int> EventKinds(IdleMotionController cat, int count)
        {
            List<int> kinds = new List<int>();
            int previous = 0;
            for (int time = 20; time <= count * 17000 && kinds.Count < count; time += 20)
            {
                cat.Tick(20, true);
                int current = Kind(cat.Pose);
                if (current != 0 && previous == 0) kinds.Add(current);
                previous = current;
            }
            Assert(kinds.Count == count, "Not enough complete event starts were observed.");
            return kinds;
        }

        private static void IrregularRests()
        {
            IdleMotionController cat = new IdleMotionController(7);
            int previousKind = 0, lastEnd = -1, onset = -1;
            List<int> rests = new List<int>();
            for (int time = 20; time <= 240000; time += 20)
            {
                cat.Tick(20, true);
                int kind = Kind(cat.Pose);
                if (kind != 0 && previousKind == 0)
                {
                    if (lastEnd >= 0) rests.Add(time - lastEnd);
                    onset = time;
                }
                if (kind == 0 && previousKind != 0)
                {
                    Assert(time - onset <= (previousKind == 3 ? 1500 : 800), "A local gesture kept moving too long.");
                    lastEnd = time;
                }
                previousKind = kind;
            }
            Assert(rests.Count >= 10, "Too few events to inspect spacing.");
            HashSet<int> distinct = new HashSet<int>();
            foreach (int rest in rests)
            {
                Assert(rest >= 4960 && rest <= 15040, "Quiet rest was outside the designed five-to-fifteen-second range.");
                distinct.Add(rest);
            }
            Assert(distinct.Count >= 5, "Schedule used mechanically equal intervals.");
        }

        private static void Bounds()
        {
            for (int seed = -15; seed <= 15; seed++)
            {
                IdleMotionController cat = new IdleMotionController(seed);
                for (int i = 0; i < 1600; i++)
                {
                    cat.Tick(37, true);
                    IdleMotionPose p = cat.Pose;
                    AssertFinite(p);
                    Assert(Math.Abs(p.LeftEarDegrees) <= 8.000001 && Math.Abs(p.RightEarDegrees) <= 8.000001, "Ear angle exceeded bounds.");
                    Assert(p.TailLift >= 0 && p.TailLift <= 1 && Math.Abs(p.TailSway) <= 1, "Tail normalization exceeded bounds.");
                }
            }
        }

        private static void AssertFinite(IdleMotionPose p)
        {
            double[] values = new double[] { p.LeftEarDegrees, p.RightEarDegrees, p.TailLift, p.TailSway };
            foreach (double value in values) Assert(!Double.IsNaN(value) && !Double.IsInfinity(value), "Pose became nonfinite.");
        }

        private static void Continuity()
        {
            IdleMotionController cat = new IdleMotionController(31);
            IdleMotionPose previous = cat.Pose;
            for (int time = 5; time <= 180000; time += 5)
            {
                cat.Tick(5, time % 19000 < 17000);
                IdleMotionPose next = cat.Pose;
                Assert(Math.Abs(next.LeftEarDegrees - previous.LeftEarDegrees) < .6 && Math.Abs(next.RightEarDegrees - previous.RightEarDegrees) < .6, "Ear pose jumped between five-millisecond samples.");
                Assert(Math.Abs(next.TailLift - previous.TailLift) < .045 && Math.Abs(next.TailSway - previous.TailSway) < .045, "Tail pose jumped between five-millisecond samples.");
                previous = next;
            }
        }

        private static void FindVisibleMotion(IdleMotionController cat)
        {
            for (int i = 0; i < 600; i++)
            {
                cat.Tick(20, true);
                if (Magnitude(cat.Pose) > .1) return;
            }
            throw new Exception("No visible startup gesture found.");
        }

        private static void Pause()
        {
            IdleMotionController cat = new IdleMotionController(42);
            IdleMotionController baseline = new IdleMotionController(42);
            FindVisibleMotion(cat); FindVisibleMotion(baseline);
            IdleMotionPose before = cat.Pose;
            cat.Paused = true;
            cat.Tick(10000, true); cat.Tick(10000, false); cat.Tick(Double.MaxValue, true);
            Near(cat.Pose, before, 0, "Pause changed the pose.");
            cat.Paused = false;
            for (int i = 0; i < 1000; i++)
            {
                cat.Tick(20, true); baseline.Tick(20, true);
                Near(cat.Pose, baseline.Pose, 0, "Paused ticks changed the active flag or schedule.");
            }
        }

        private static void InactiveSettling()
        {
            IdleMotionController cat = new IdleMotionController(7);
            FindVisibleMotion(cat);
            IdleMotionPose initial = cat.Pose;
            cat.Tick(110, false);
            IdleMotionPose half = cat.Pose;
            Assert(Magnitude(half) > 0, "Suppression snapped immediately to neutral.");
            Near(half, new IdleMotionPose { LeftEarDegrees = initial.LeftEarDegrees / 2, RightEarDegrees = initial.RightEarDegrees / 2, TailLift = initial.TailLift / 2, TailSway = initial.TailSway / 2 }, 1e-10, "Settling did not ease halfway after 110ms.");
            cat.Tick(110, false);
            Assert(Magnitude(cat.Pose) == 0, "Suppression did not settle after 220ms.");
            for (int i = 0; i < 100; i++) { cat.Tick(1000, false); Assert(Magnitude(cat.Pose) == 0, "Inactive state initiated a gesture."); }
        }

        private static void ResumeBuffer()
        {
            IdleMotionController cat = new IdleMotionController(17);
            FindVisibleMotion(cat); cat.Tick(220, false); cat.Tick(10000, false);
            int first = 0;
            for (int time = 20; time <= 11000; time += 20)
            {
                cat.Tick(20, true);
                if (Kind(cat.Pose) != 0) { first = time; break; }
            }
            Assert(first >= 5000 && first <= 10040, "Resume did not leave a five-to-ten-second quiet buffer.");
        }

        private static void EarlyResume()
        {
            IdleMotionController cat = new IdleMotionController(7);
            FindVisibleMotion(cat); cat.Tick(80, false);
            double prior = Magnitude(cat.Pose);
            cat.Tick(20, true);
            Assert(Magnitude(cat.Pose) > 0 && Magnitude(cat.Pose) < prior, "Early resume interrupted the settling or snapped.");
            cat.Tick(120, true);
            Assert(Magnitude(cat.Pose) == 0, "Early resume did not finish the original 220ms settling.");
            cat.Tick(4500, true);
            Assert(Magnitude(cat.Pose) == 0, "Early resume started another gesture too soon.");
        }

        private static void InvalidTime()
        {
            IdleMotionController cat = new IdleMotionController(12);
            IdleMotionController baseline = new IdleMotionController(12);
            FindVisibleMotion(cat); FindVisibleMotion(baseline);
            double[] values = new double[] { -1, -Double.MaxValue, 0, Double.NaN, Double.PositiveInfinity, Double.NegativeInfinity };
            foreach (double value in values) { cat.Tick(value, false); cat.Tick(value, true); }
            for (int i = 0; i < 1000; i++)
            {
                cat.Tick(20, true); baseline.Tick(20, true);
                Near(cat.Pose, baseline.Pose, 0, "Invalid time changed the timeline or random schedule.");
            }
        }

        private static void LargeGap()
        {
            IdleMotionController cat = new IdleMotionController(Int32.MinValue);
            FindVisibleMotion(cat);
            double[] values = new double[] { 60000, 1e12, Double.MaxValue };
            foreach (double value in values)
            {
                cat.Tick(value, true);
                Assert(Magnitude(cat.Pose) == 0, "Sleep gap replayed an old gesture.");
                cat.Tick(1000, true);
                Assert(Magnitude(cat.Pose) == 0, "Sleep recovery did not leave quiet space.");
                cat.Tick(value, false);
                AssertFinite(cat.Pose);
                Assert(Magnitude(cat.Pose) == 0, "Inactive sleep gap did not remain neutral.");
            }
        }

        private static void DeterminismAndReset()
        {
            IdleMotionController cat = new IdleMotionController(91);
            IdleMotionController baseline = new IdleMotionController(91);
            for (int i = 0; i < 2000; i++) cat.Tick(23, i % 200 < 180);
            cat.Paused = true; cat.Reset();
            Assert(!cat.Paused && Magnitude(cat.Pose) == 0, "Reset did not clear pause and pose.");
            for (int i = 0; i < 4000; i++)
            {
                bool active = i % 500 < 470;
                cat.Tick(17, active); baseline.Tick(17, active);
                Near(cat.Pose, baseline.Pose, 0, "Reset did not reproduce its constructor seed.");
            }
        }

        private static void Partitioning()
        {
            IdleMotionController large = new IdleMotionController(31);
            IdleMotionController small = new IdleMotionController(31);
            for (int i = 0; i < 1500; i++)
            {
                large.Tick(100, true);
                for (int j = 0; j < 5; j++) small.Tick(20, true);
                Near(large.Pose, small.Pose, 1e-9, "Ordinary time partitioning changed a pose.");
            }
        }
    }
}
