using System;
using System.Collections.Generic;

namespace BaobaoDesktop
{
    public enum InteractionKind { Idle, Eat, Drink, Pet, Play, Scratch, Climb }

    /// <summary>
    /// Pure logical playback for the 44-frame atlas. Only a completed return to
    /// idle emits Completed. Call from a single UI thread.
    /// </summary>
    public sealed class InteractionController
    {
        private struct Step
        {
            public readonly int Frame;
            public readonly double Milliseconds;
            public Step(int frame, double milliseconds) { Frame = frame; Milliseconds = milliseconds; }
        }

        private enum Purpose { Idle, FiniteAction, PetEntry, PetLoop, PlayTracking, Returning }
        private static Step S(int frame, double milliseconds) { return new Step(frame, milliseconds); }
        private static readonly Step[] IdleSteps = new Step[] { S(0, 3700), S(1, 75), S(2, 110), S(3, 85) };
        private static readonly Step[] EatSteps = new Step[]
        {
            S(0,180), S(4,260), S(5,260), S(6,240), S(7,240),
            S(8,180), S(7,180), S(8,180), S(7,180), S(8,180), S(7,180), S(8,180),
            S(9,250), S(10,300), S(11,260), S(0,360)
        };
        private static readonly Step[] DrinkSteps = new Step[]
        {
            S(0,180), S(12,260), S(13,280), S(14,260),
            S(15,180), S(16,180), S(17,180), S(15,180), S(16,180), S(17,180), S(15,180), S(16,180), S(17,180),
            S(18,300), S(19,260), S(0,360)
        };
        private static readonly Step[] PetEntrySteps = new Step[] { S(20, 360) };
        private static readonly Step[] PetLoopSteps = new Step[] { S(21, 1000), S(22, 1000) };
        private static readonly Step[] ScratchSteps = new Step[]
        {
            S(28,240), S(29,280),
            S(30,190), S(31,190), S(32,210), S(33,210),
            S(30,190), S(31,190), S(32,210), S(33,210),
            S(34,300), S(35,260), S(0,360)
        };
        private static readonly Step[] ClimbSteps = new Step[]
        {
            S(36,300), S(37,350), S(38,400), S(39,400), S(40,1100),
            S(41,400), S(42,350), S(43,300), S(0,360)
        };
        private const double PlayCycleMilliseconds = 2200;
        private const double PawStartMilliseconds = 1200;
        private const double PawEndMilliseconds = 1800;
        private const double PlayFacingHoldMilliseconds = 300;
        private const double PetEntryMilliseconds = 320;
        private const double PetExitMilliseconds = 500;
        private const double PetSwayPeriodMilliseconds = 3200;
        private const double PetTailPeriodMilliseconds = 4800;
        private const double PetBreathPeriodMilliseconds = 4000;
        private const double PetEarPeriodMilliseconds = 8400;

        private readonly int seed;
        private Random random;
        private Purpose purpose;
        private Step[] steps;
        private int stepIndex;
        private double stepElapsed;
        private double sequenceDuration;
        private bool completeOnReturn;
        private double playCycleElapsed;
        private int playDirection;
        private int playTargetDirection;
        private int pawDirection;
        private double playFacingHoldRemaining;
        private double petEntryElapsed;
        private double petExitElapsed;
        private double petExitStartBlend;
        private double petPhase;
        private double petTailPhase;
        private double petBreathPhase;
        private double petEarPhase;
        private bool petExiting;

        public int FrameIndex { get; private set; }
        public bool Paused { get; set; }
        public bool IsBusy { get { return CurrentKind != InteractionKind.Idle; } }
        public InteractionKind CurrentKind { get; private set; }
        public double PetBlend { get; private set; }
        public double PetSway { get; private set; }
        public double PetTailLift { get; private set; }
        public double PetTailSway { get; private set; }
        public double PetLeftEarDegrees { get; private set; }
        public double PetRightEarDegrees { get; private set; }
        public double PetBreath { get; private set; }
        public event Action<InteractionKind> Completed;

        public InteractionController() : this(Environment.TickCount) { }
        public InteractionController(int seed) { this.seed = seed; Reset(); }

        public bool TryStart(InteractionKind kind)
        {
            if (IsBusy || (kind != InteractionKind.Eat && kind != InteractionKind.Drink && kind != InteractionKind.Pet && kind != InteractionKind.Play && kind != InteractionKind.Scratch && kind != InteractionKind.Climb)) return false;
            CurrentKind = kind;
            completeOnReturn = false;
            if (kind == InteractionKind.Eat) SetSequence(EatSteps, Purpose.FiniteAction);
            else if (kind == InteractionKind.Drink) SetSequence(DrinkSteps, Purpose.FiniteAction);
            else if (kind == InteractionKind.Pet) { ClearPetMotion(); SetSequence(PetEntrySteps, Purpose.PetEntry); }
            else if (kind == InteractionKind.Scratch) SetSequence(ScratchSteps, Purpose.FiniteAction);
            else if (kind == InteractionKind.Climb) SetSequence(ClimbSteps, Purpose.FiniteAction);
            else
            {
                purpose = Purpose.PlayTracking;
                playCycleElapsed = 0;
                playDirection = 1;
                playTargetDirection = 1;
                pawDirection = 1;
                playFacingHoldRemaining = 0;
                FrameIndex = 26;
            }
            return true;
        }

        /// <summary>Canonical action IDs; idle explicitly requests cancellation.</summary>
        public bool TryStartAction(string actionId)
        {
            switch (actionId)
            {
                case "idle": ReturnToIdle(); return true;
                case "eat": return TryStart(InteractionKind.Eat);
                case "drink": return TryStart(InteractionKind.Drink);
                case "pet": return BeginPetting();
                case "play": return BeginPlay();
                case "toy.scratcher": return TryStart(InteractionKind.Scratch);
                case "toy.cat_tree": return TryStart(InteractionKind.Climb);
                default: return false;
            }
        }

        /// <summary>A busy Feed neither consumes randomness nor changes motion.</summary>
        public bool Feed()
        {
            if (IsBusy) return false;
            InteractionKind kind = random.Next(2) == 0 ? InteractionKind.Eat : InteractionKind.Drink;
            return TryStart(kind);
        }

        public bool BeginPetting() { return TryStart(InteractionKind.Pet); }
        public bool BeginPlay() { return TryStart(InteractionKind.Play); }

        public void EndPetting(bool completed)
        {
            if (CurrentKind != InteractionKind.Pet || purpose == Purpose.Returning) return;
            BeginReturn(completed);
        }

        public void EndPlay(bool completed)
        {
            if (CurrentKind != InteractionKind.Play || purpose == Purpose.Returning) return;
            BeginReturn(completed);
        }

        /// <summary>
        /// Input is 0..1 with a .44..56 center dead zone; infinities clamp to the
        /// nearest edge and NaN is ignored. A short orientation hold filters rapid
        /// side changes. A raised paw finishes before switching its side.
        /// Paused input does not alter the visible tracking pose.
        /// </summary>
        public void SetPlayTarget(double normalizedX)
        {
            if (Paused || CurrentKind != InteractionKind.Play || purpose != Purpose.PlayTracking || Double.IsNaN(normalizedX)) return;
            double target = Math.Max(0, Math.Min(1, normalizedX));
            if (target <= .44) playTargetDirection = -1;
            else if (target >= .56) playTargetDirection = 1;
            ApplyPlayFacing();
        }

        /// <summary>Explicit cancellation also clears a pending success return.</summary>
        public void ReturnToIdle()
        {
            if (!IsBusy) { SetIdle(); return; }
            if (purpose == Purpose.Returning) { completeOnReturn = false; return; }
            BeginReturn(false);
        }

        public void Reset()
        {
            random = new Random(seed);
            Paused = false;
            SetIdle();
        }

        public void Tick(double milliseconds)
        {
            if (Paused || milliseconds <= 0 || Double.IsNaN(milliseconds) || Double.IsInfinity(milliseconds)) return;
            double remaining = milliseconds;
            while (remaining > 0 && !Paused)
            {
                if (purpose == Purpose.PlayTracking) { AdvancePlay(remaining); return; }
                double untilNext = steps[stepIndex].Milliseconds - stepElapsed;
                if (remaining < untilNext) { AdvancePetMotion(remaining); stepElapsed += remaining; return; }
                AdvancePetMotion(untilNext);
                remaining -= untilNext;
                stepElapsed = 0;
                stepIndex++;
                if (stepIndex < steps.Length) { FrameIndex = steps[stepIndex].Frame; continue; }

                if (purpose == Purpose.Idle || purpose == Purpose.PetLoop)
                {
                    stepIndex = 0;
                    if (remaining >= sequenceDuration)
                    {
                        double remainder = remaining % sequenceDuration;
                        AdvancePetMotion(remaining - remainder);
                        remaining = remainder;
                    }
                    FrameIndex = steps[0].Frame;
                }
                else if (purpose == Purpose.PetEntry) SetSequence(PetLoopSteps, Purpose.PetLoop);
                else
                {
                    bool completed = purpose == Purpose.FiniteAction || completeOnReturn;
                    FinishAction(completed);
                    // A completion handler may deliberately start another action.
                    // That action starts on the next tick rather than receiving a
                    // huge stale delta and recursively completing many actions.
                    if (IsBusy || Paused) return;
                }
            }
        }

        private void AdvancePlay(double milliseconds)
        {
            double untilPaw = playCycleElapsed < PawStartMilliseconds
                ? PawStartMilliseconds - playCycleElapsed
                : PlayCycleMilliseconds + PawStartMilliseconds - playCycleElapsed;
            bool crossedPawStart = milliseconds >= untilPaw;
            playCycleElapsed = (playCycleElapsed + milliseconds % PlayCycleMilliseconds) % PlayCycleMilliseconds;
            playFacingHoldRemaining = Math.Max(0, playFacingHoldRemaining - milliseconds);
            if (PawIsExtended())
            {
                if (crossedPawStart) pawDirection = playDirection;
                FrameIndex = pawDirection < 0 ? 25 : 27;
            }
            else ApplyPlayFacing();
        }

        private void ApplyPlayFacing()
        {
            if (PawIsExtended()) return;
            if (playFacingHoldRemaining <= 0 && playDirection != playTargetDirection)
            {
                playDirection = playTargetDirection;
                playFacingHoldRemaining = PlayFacingHoldMilliseconds;
            }
            FrameIndex = playDirection < 0 ? 24 : 26;
        }

        private void AdvancePetMotion(double milliseconds)
        {
            if (CurrentKind != InteractionKind.Pet || milliseconds <= 0) return;
            petPhase = (petPhase + milliseconds % PetSwayPeriodMilliseconds) % PetSwayPeriodMilliseconds;
            // These clocks deliberately do not borrow the wrapping head phase:
            // the 3.2s head wrap must not restart a 4.8s tail or 4s breath.
            petTailPhase = (petTailPhase + milliseconds % PetTailPeriodMilliseconds) % PetTailPeriodMilliseconds;
            petBreathPhase = (petBreathPhase + milliseconds % PetBreathPeriodMilliseconds) % PetBreathPeriodMilliseconds;
            petEarPhase = (petEarPhase + milliseconds % PetEarPeriodMilliseconds) % PetEarPeriodMilliseconds;
            if (petExiting)
            {
                petExitElapsed = Math.Min(PetExitMilliseconds, petExitElapsed + milliseconds);
                PetBlend = petExitStartBlend * (1 - Smooth(petExitElapsed / PetExitMilliseconds));
            }
            else
            {
                petEntryElapsed = Math.Min(PetEntryMilliseconds, petEntryElapsed + milliseconds);
                PetBlend = Smooth(petEntryElapsed / PetEntryMilliseconds);
            }
            PetSway = Math.Sin(petPhase * 2 * Math.PI / PetSwayPeriodMilliseconds) * PetBlend;
            double tailAngle = petTailPhase * 2 * Math.PI / PetTailPeriodMilliseconds;
            PetTailLift = .65 * (1 - Math.Cos(tailAngle)) * .5 * PetBlend;
            PetTailSway = .55 * Math.Sin(tailAngle) * PetBlend;
            PetBreath = (1 - Math.Cos(petBreathPhase * 2 * Math.PI / PetBreathPeriodMilliseconds)) * .5 * PetBlend;
            PetLeftEarDegrees = -3 * EarEnvelope(petEarPhase - 950) * PetBlend;
            PetRightEarDegrees = 2.7 * EarEnvelope(petEarPhase - 4650) * PetBlend;
        }

        private static double EarEnvelope(double elapsed)
        {
            if (elapsed <= 0 || elapsed >= 700) return 0;
            if (elapsed < 200) return Smooth(elapsed / 200);
            if (elapsed < 300) return 1;
            return 1 - Smooth((elapsed - 300) / 400);
        }

        private static double Smooth(double t)
        {
            if (t <= 0) return 0;
            if (t >= 1) return 1;
            return t * t * t * (t * (t * 6 - 15) + 10);
        }

        private void ClearPetMotion()
        {
            petEntryElapsed = 0; petExitElapsed = 0; petExitStartBlend = 0;
            petPhase = 0; petExiting = false; PetBlend = 0; PetSway = 0;
            petTailPhase = 0; petBreathPhase = 0; petEarPhase = 0;
            PetTailLift = 0; PetTailSway = 0; PetLeftEarDegrees = 0; PetRightEarDegrees = 0; PetBreath = 0;
        }

        private bool PawIsExtended()
        {
            return playCycleElapsed >= PawStartMilliseconds && playCycleElapsed < PawEndMilliseconds;
        }

        private void BeginReturn(bool completed)
        {
            if (FrameIndex == 0)
            {
                // Cancelling before an action has visibly begun has no outcome.
                SetIdle();
                return;
            }
            List<Step> returning = new List<Step>();
            returning.Add(S(FrameIndex, 60));
            if (CurrentKind == InteractionKind.Eat)
            {
                if (FrameIndex <= 6)
                {
                    for (int frame = FrameIndex - 1; frame >= 4; frame--) returning.Add(S(frame, 180));
                    returning.Add(S(11, 180));
                }
                else for (int frame = Math.Max(9, FrameIndex + 1); frame <= 11; frame++) returning.Add(S(frame, 220));
            }
            else if (CurrentKind == InteractionKind.Drink)
            {
                if (FrameIndex <= 14)
                {
                    for (int frame = FrameIndex - 1; frame >= 12; frame--) returning.Add(S(frame, 180));
                    returning.Add(S(19, 180));
                }
                else for (int frame = Math.Max(18, FrameIndex + 1); frame <= 19; frame++) returning.Add(S(frame, 220));
            }
            else if (CurrentKind == InteractionKind.Pet)
            {
                petExiting = true; petExitElapsed = 0; petExitStartBlend = PetBlend;
                returning.Add(S(23, 260));
            }
            else if (CurrentKind == InteractionKind.Play)
            {
                if (FrameIndex == 25) returning.Add(S(24, 180));
                else if (FrameIndex == 27) returning.Add(S(26, 180));
            }
            else if (CurrentKind == InteractionKind.Scratch)
            {
                if (FrameIndex != 35) returning.Add(S(35, 260));
            }
            else if (CurrentKind == InteractionKind.Climb)
            {
                if (FrameIndex == 39 || FrameIndex == 40) returning.Add(S(41, 300));
                if (FrameIndex >= 38 && FrameIndex <= 41) returning.Add(S(42, 300));
                if (FrameIndex != 43) returning.Add(S(43, 260));
            }
            returning.Add(S(0, 180));
            completeOnReturn = completed;
            SetSequence(returning.ToArray(), Purpose.Returning);
        }

        private void FinishAction(bool completed)
        {
            InteractionKind finished = CurrentKind;
            SetIdle();
            Action<InteractionKind> handler = Completed;
            if (completed && finished != InteractionKind.Idle && handler != null) handler(finished);
        }

        private void SetIdle()
        {
            CurrentKind = InteractionKind.Idle;
            completeOnReturn = false;
            playCycleElapsed = 0;
            playDirection = 1;
            playTargetDirection = 1;
            pawDirection = 1;
            playFacingHoldRemaining = 0;
            ClearPetMotion();
            SetSequence(IdleSteps, Purpose.Idle);
        }

        private void SetSequence(Step[] sequence, Purpose nextPurpose)
        {
            steps = sequence;
            purpose = nextPurpose;
            stepIndex = 0;
            stepElapsed = 0;
            sequenceDuration = 0;
            foreach (Step step in sequence) sequenceDuration += step.Milliseconds;
            FrameIndex = steps[0].Frame;
        }
    }
}
