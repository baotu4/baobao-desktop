using System;
using System.Reflection;

namespace BaobaoDesktop
{
    public static class PetDetailRegressionTests
    {
        private static readonly string[] Names = new string[] { "PetTailLift", "PetTailSway", "PetLeftEarDegrees", "PetRightEarDegrees", "PetBreath" };
        private static int failures;

        public static int Main()
        {
            Run("pet detail parameters are publicly available", PublicParameters);
            Run("a short pet visibly includes tail, one ear and breathing", ShortPetShowsDetails);
            Run("ear gestures stay single and details stay within subtle bounds", SingleEarAndBounds);
            Run("details stay continuous across head-clock wrap points", IndependentClockContinuity);
            Run("tail and breath retain their independent periods", IndependentPeriods);
            Run("every detail follows the pet entry and exit envelope", BlendEnvelope);
            Run("pause freezes all details and completed exit clears them", PauseAndCompletion);
            Run("idle, reset and other interactions have no pet offsets", NoLeaks);
            Run("large pet ticks remain finite without ending the session", LargeTicks);
            Run("different normal tick partitions produce the same detail pose", TickPartitioning);
            Console.WriteLine(failures == 0 ? "PASS: 10 pet-detail regressions" : "FAIL: " + failures + " pet-detail regressions");
            return failures == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            try { test(); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
        }
        private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
        private static double Read(InteractionController cat, string name)
        {
            PropertyInfo property = typeof(InteractionController).GetProperty(name);
            Assert(property != null && property.PropertyType == typeof(double) && property.CanRead, "Missing continuous detail " + name + ".");
            return (double)property.GetValue(cat, null);
        }
        private static double[] Snapshot(InteractionController cat)
        {
            double[] result = new double[Names.Length];
            for (int i = 0; i < Names.Length; i++) result[i] = Read(cat, Names[i]);
            return result;
        }
        private static void Near(double[] a, double[] b, double tolerance, string message)
        {
            for (int i = 0; i < a.Length; i++) Assert(Math.Abs(a[i] - b[i]) <= tolerance, message + " (" + Names[i] + ")");
        }
        private static void Neutral(InteractionController cat)
        {
            foreach (double value in Snapshot(cat)) Assert(value == 0, "Pet detail remained after idle/reset.");
        }
        private static void Bounds(InteractionController cat)
        {
            double[] p = Snapshot(cat); double blend = cat.PetBlend;
            foreach (double value in p) Assert(!Double.IsNaN(value) && !Double.IsInfinity(value), "Detail became nonfinite.");
            Assert(p[0] >= 0 && p[0] <= blend + 1e-10 && Math.Abs(p[1]) <= blend + 1e-10, "Tail escaped the pet envelope.");
            Assert(Math.Abs(p[2]) <= 3.5 * blend + 1e-10 && Math.Abs(p[3]) <= 3.5 * blend + 1e-10, "Pet ear exceeded its gentle amplitude/envelope.");
            Assert(Math.Abs(p[2]) < 1e-10 || Math.Abs(p[3]) < 1e-10, "Both ears moved together.");
            Assert(p[4] >= 0 && p[4] <= blend + 1e-10, "Breathing escaped the pet envelope.");
        }
        private static void PublicParameters()
        {
            InteractionController cat = new InteractionController();
            Neutral(cat); cat.BeginPetting(); Neutral(cat);
        }
        private static void ShortPetShowsDetails()
        {
            InteractionController cat = new InteractionController(); cat.BeginPetting();
            double tail = 0, ear = 0, breath = 0;
            for (int time = 10; time <= 2600; time += 10)
            {
                cat.Tick(10); tail = Math.Max(tail, Read(cat, "PetTailLift"));
                ear = Math.Max(ear, Math.Max(Math.Abs(Read(cat, "PetLeftEarDegrees")), Math.Abs(Read(cat, "PetRightEarDegrees"))));
                breath = Math.Max(breath, Read(cat, "PetBreath"));
            }
            Console.WriteLine("OBSERVE first2600ms tailPeak=" + tail.ToString("F3") + " earPeakDegrees=" + ear.ToString("F3") + " breathPeak=" + breath.ToString("F3"));
            Assert(tail > .2 && ear > 1 && breath > .4, "Short pet still changes only the head.");
        }
        private static void SingleEarAndBounds()
        {
            InteractionController cat = new InteractionController(); cat.BeginPetting(); bool left = false, right = false;
            for (int time = 0; time < 20000; time += 10)
            {
                cat.Tick(10); Bounds(cat);
                if (Math.Abs(Read(cat, "PetLeftEarDegrees")) > .1) left = true;
                if (Math.Abs(Read(cat, "PetRightEarDegrees")) > .1) right = true;
            }
            Assert(left && right, "Longer pet never showed independently staggered ear gestures.");
        }
        private static void IndependentClockContinuity()
        {
            InteractionController cat = new InteractionController(); cat.BeginPetting(); double[] previous = Snapshot(cat);
            for (int time = 10; time <= 30000; time += 10)
            {
                cat.Tick(10); double[] next = Snapshot(cat);
                Assert(Math.Abs(next[0] - previous[0]) < .03 && Math.Abs(next[1] - previous[1]) < .03 && Math.Abs(next[4] - previous[4]) < .03, "Tail/breath jumped at a clock boundary.");
                Assert(Math.Abs(next[2] - previous[2]) < .5 && Math.Abs(next[3] - previous[3]) < .5, "Single-ear envelope jumped.");
                previous = next;
            }
        }
        private static void IndependentPeriods()
        {
            InteractionController tail = new InteractionController(); tail.BeginPetting(); tail.Tick(400);
            double lift = Read(tail, "PetTailLift"), sway = Read(tail, "PetTailSway"); tail.Tick(4800);
            Assert(Math.Abs(Read(tail, "PetTailLift") - lift) < 1e-9 && Math.Abs(Read(tail, "PetTailSway") - sway) < 1e-9, "Tail borrowed the head's wrapping clock.");
            InteractionController breath = new InteractionController(); breath.BeginPetting(); breath.Tick(400);
            double value = Read(breath, "PetBreath"); breath.Tick(4000);
            Assert(Math.Abs(Read(breath, "PetBreath") - value) < 1e-9, "Breathing borrowed another component's wrapping clock.");
        }
        private static void BlendEnvelope()
        {
            InteractionController cat = new InteractionController(); int completed = 0; cat.Completed += delegate { completed++; }; cat.BeginPetting();
            for (int time = 0; time < 1200; time += 10) { cat.Tick(10); Bounds(cat); }
            cat.EndPetting(false);
            for (int time = 0; time < 500; time += 10) { cat.Tick(10); Bounds(cat); }
            Assert(!cat.IsBusy && completed == 0, "Cancelled detail fade changed business completion."); Neutral(cat);
        }
        private static void PauseAndCompletion()
        {
            InteractionController cat = new InteractionController(); int completed = 0; cat.Completed += delegate { completed++; }; cat.BeginPetting(); cat.Tick(1200);
            double[] before = Snapshot(cat); cat.Paused = true; cat.Tick(10000); cat.Tick(Double.MaxValue); Near(Snapshot(cat), before, 0, "Pause advanced a detail.");
            cat.EndPetting(true); cat.Tick(10000); Near(Snapshot(cat), before, 0, "Paused return changed a detail.");
            cat.Paused = false; cat.Tick(500); Assert(!cat.IsBusy && completed == 1, "Normal detail fade did not finish once."); Neutral(cat);
        }
        private static void NoLeaks()
        {
            InteractionController cat = new InteractionController(); cat.BeginPetting(); cat.Tick(1200); cat.Reset(); Neutral(cat);
            foreach (InteractionKind kind in new InteractionKind[] { InteractionKind.Eat, InteractionKind.Drink, InteractionKind.Play, InteractionKind.Scratch, InteractionKind.Climb })
            {
                Assert(cat.TryStart(kind), "Idle action could not start."); cat.Tick(1000); Neutral(cat); cat.Reset(); Neutral(cat);
            }
        }
        private static void LargeTicks()
        {
            InteractionController cat = new InteractionController(); int completed = 0; cat.Completed += delegate { completed++; }; cat.BeginPetting(); cat.Tick(Double.MaxValue);
            Bounds(cat); Assert(cat.IsBusy && completed == 0, "Large pet time ended the held session.");
            cat.EndPetting(true); cat.Tick(Double.MaxValue); Neutral(cat); Assert(!cat.IsBusy && completed == 1, "Large return did not finish safely.");
        }
        private static void TickPartitioning()
        {
            InteractionController a = new InteractionController(9), b = new InteractionController(9); a.BeginPetting(); b.BeginPetting();
            for (int i = 0; i < 1000; i++)
            {
                a.Tick(40); for (int j = 0; j < 4; j++) b.Tick(10);
                Near(Snapshot(a), Snapshot(b), 1e-8, "Frame partition changed a detail clock.");
            }
        }
    }
}
