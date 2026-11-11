using System;

namespace BaobaoDesktop
{
    public struct IdleMotionPose
    {
        public double LeftEarDegrees;
        public double RightEarDegrees;
        public double TailLift;
        public double TailSway;
    }

    /// <summary>
    /// Schedules sparse local idle motions, independently of the image renderer.
    /// All times and amplitudes are visual design parameters, not measurements
    /// of feline physiology. Use on a single UI thread.
    /// </summary>
    public sealed class IdleMotionController
    {
        private enum MotionKind { None, LeftEar, RightEar, Tail }
        private const double SettleMilliseconds = 220;
        private const double SleepGapMilliseconds = 60000;
        private readonly int seed;
        private Random random;
        private IdleMotionPose pose;
        private IdleMotionPose settleFrom;
        private MotionKind motion;
        private bool wasActive;
        private bool settling;
        private double settleElapsed;
        private double waitRemaining;
        private double motionElapsed;
        private double outDuration;
        private double holdDuration;
        private double backDuration;
        private double earAmplitude;
        private double liftAmplitude;
        private double swayAmplitude;
        private int startupMotionIndex;
        private bool lastWasTail;
        private int repeatedTypeCount;

        public IdleMotionController() : this(Environment.TickCount) { }

        public IdleMotionController(int seed)
        {
            this.seed = seed;
            Reset();
        }

        public IdleMotionPose Pose { get { return pose; } }

        public bool Paused { get; set; }

        /// <summary>
        /// Negative, zero and nonfinite ticks have no effect. Paused freezes all
        /// state, including an active change. A gap of a minute or more is treated
        /// as waking from sleep: discard old events and wait before moving again.
        /// </summary>
        public void Tick(double milliseconds, bool active)
        {
            if (Paused || milliseconds <= 0 || Double.IsNaN(milliseconds) || Double.IsInfinity(milliseconds))
                return;

            if (milliseconds >= SleepGapMilliseconds)
            {
                ClearMotion();
                wasActive = active;
                waitRemaining = active ? Between(5000, 10000) : 0;
                return;
            }

            if (!active)
            {
                if (wasActive)
                {
                    wasActive = false;
                    motion = MotionKind.None;
                    motionElapsed = 0;
                    waitRemaining = 0;
                    if (!settling) BeginSettle();
                }
                double ignored = milliseconds;
                AdvanceSettle(ref ignored);
                return;
            }

            if (!wasActive)
            {
                wasActive = true;
                // Finish any in-flight settling first, then leave quiet space
                // after eating or revealing the ornament.
                waitRemaining = Between(5000, 10000);
            }

            double remaining = milliseconds;
            AdvanceSettle(ref remaining);
            if (settling || remaining <= 0) return;

            while (remaining > 0)
            {
                if (motion == MotionKind.None)
                {
                    if (remaining < waitRemaining)
                    {
                        waitRemaining -= remaining;
                        return;
                    }
                    remaining -= waitRemaining;
                    waitRemaining = 0;
                    BeginMotion();
                }
                else
                {
                    double duration = outDuration + holdDuration + backDuration;
                    double untilEnd = duration - motionElapsed;
                    if (remaining < untilEnd)
                    {
                        motionElapsed += remaining;
                        PublishMotion();
                        return;
                    }
                    remaining -= untilEnd;
                    motion = MotionKind.None;
                    motionElapsed = 0;
                    pose = new IdleMotionPose();
                    // Events never overlap; a completed gesture rests before
                    // another single ear or tail gesture is selected.
                    waitRemaining = Between(5000, 15000);
                }
            }
        }

        /// <summary>Reset is reproducible for the constructor seed and unpauses.</summary>
        public void Reset()
        {
            random = new Random(seed);
            Paused = false;
            ClearMotion();
            wasActive = true;
            startupMotionIndex = 0;
            lastWasTail = false;
            repeatedTypeCount = 0;
            waitRemaining = Between(2000, 4000);
        }

        private void ClearMotion()
        {
            pose = new IdleMotionPose();
            settleFrom = new IdleMotionPose();
            motion = MotionKind.None;
            settling = false;
            settleElapsed = 0;
            motionElapsed = 0;
            waitRemaining = 0;
        }

        private void BeginSettle()
        {
            settleFrom = pose;
            settleElapsed = 0;
            settling = !IsNeutral(pose);
            if (!settling) pose = new IdleMotionPose();
        }

        private void AdvanceSettle(ref double remaining)
        {
            if (!settling) return;
            double untilEnd = SettleMilliseconds - settleElapsed;
            if (remaining >= untilEnd)
            {
                remaining -= untilEnd;
                settleElapsed = SettleMilliseconds;
                settling = false;
                pose = new IdleMotionPose();
                return;
            }
            settleElapsed += remaining;
            remaining = 0;
            double scale = 1 - Smooth(settleElapsed / SettleMilliseconds);
            pose = new IdleMotionPose
            {
                LeftEarDegrees = settleFrom.LeftEarDegrees * scale,
                RightEarDegrees = settleFrom.RightEarDegrees * scale,
                TailLift = settleFrom.TailLift * scale,
                TailSway = settleFrom.TailSway * scale
            };
        }

        private void BeginMotion()
        {
            bool tail;
            if (startupMotionIndex == 0)
            {
                tail = true;
                startupMotionIndex = 1;
            }
            else if (startupMotionIndex == 1)
            {
                tail = false;
                startupMotionIndex = 2;
            }
            else if (repeatedTypeCount >= 2)
            {
                tail = !lastWasTail;
            }
            else
            {
                // This is preview choreography, not a biological frequency:
                // make tail detail easy to see while retaining irregularity.
                tail = random.NextDouble() < .45;
            }
            repeatedTypeCount = repeatedTypeCount == 0 || tail != lastWasTail ? 1 : repeatedTypeCount + 1;
            lastWasTail = tail;
            motion = tail ? MotionKind.Tail : random.Next(2) == 0 ? MotionKind.LeftEar : MotionKind.RightEar;
            motionElapsed = 0;
            pose = new IdleMotionPose();
            if (motion == MotionKind.Tail)
            {
                outDuration = Between(360, 520);
                holdDuration = Between(100, 220);
                backDuration = Between(440, 680);
                liftAmplitude = Between(.35, .9);
                swayAmplitude = Between(.2, .85) * (random.Next(2) == 0 ? -1 : 1);
            }
            else
            {
                outDuration = Between(150, 230);
                holdDuration = Between(70, 150);
                backDuration = Between(220, 340);
                double outward = motion == MotionKind.LeftEar ? -1 : 1;
                earAmplitude = Between(3, 6) * outward * (random.NextDouble() < .68 ? 1 : -1);
            }
        }

        private void PublishMotion()
        {
            double amount;
            if (motionElapsed < outDuration) amount = Smooth(motionElapsed / outDuration);
            else if (motionElapsed < outDuration + holdDuration) amount = 1;
            else amount = 1 - Smooth((motionElapsed - outDuration - holdDuration) / backDuration);

            pose = new IdleMotionPose();
            if (motion == MotionKind.LeftEar) pose.LeftEarDegrees = earAmplitude * amount;
            else if (motion == MotionKind.RightEar) pose.RightEarDegrees = earAmplitude * amount;
            else if (motion == MotionKind.Tail)
            {
                // One smooth lift/sway and return, not a repeating wagging wave.
                pose.TailLift = liftAmplitude * amount;
                pose.TailSway = swayAmplitude * amount;
            }
        }

        private double Between(double minimum, double maximum)
        {
            return minimum + random.NextDouble() * (maximum - minimum);
        }

        private static bool IsNeutral(IdleMotionPose value)
        {
            return value.LeftEarDegrees == 0 && value.RightEarDegrees == 0 && value.TailLift == 0 && value.TailSway == 0;
        }

        private static double Smooth(double t)
        {
            if (t <= 0) return 0;
            if (t >= 1) return 1;
            return t * t * t * (t * (t * 6 - 15) + 10);
        }
    }
}
