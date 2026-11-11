using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace BaobaoDesktop
{
    /// <summary>
    /// Deterministic, accelerated 24-hour integration soak of the real logical
    /// controllers. No UI, recycling, user settings, file mutation or real waits.
    /// The shadow pair receives the same accepted input but does not receive
    /// rejected busy requests/invalid deltas, exposing hidden phase/RNG drift.
    /// </summary>
    public static class V1SoakTests
    {
        private const int Seed=20261110;
        private const double DayMilliseconds=24.0*60*60*1000;
        private static readonly InteractionKind[] Kinds=new InteractionKind[] {
            InteractionKind.Eat,InteractionKind.Drink,InteractionKind.Pet,
            InteractionKind.Play,InteractionKind.Scratch,InteractionKind.Climb
        };
        private static readonly string[] ActionIds=new string[] {"eat","drink","pet","play","toy.scratcher","toy.cat_tree"};
        private static readonly Dictionary<InteractionKind,long> Started=new Dictionary<InteractionKind,long>();
        private static readonly Dictionary<InteractionKind,long> Completed=new Dictionary<InteractionKind,long>();
        private static readonly Dictionary<InteractionKind,long> Cancelled=new Dictionary<InteractionKind,long>();
        private static readonly HashSet<long> CompletedTokens=new HashSet<long>();
        private static readonly HashSet<int> SeenFrames=new HashSet<int>();
        private static InteractionController cat,shadow;
        private static IdleMotionController motion,shadowMotion;
        private static Token active;
        private static long nextToken,completionEvents,shadowEvents,assertions,ticks,busyProbes,invalidProbes,pauseToggles,cancelRequests,sleepSteps,longSteps;
        private static long normalSessionEnds,cancelledSessionEnds,unknownCommands,observedGestures;
        private static double simulated,busyActiveMilliseconds,maxEar,maxLift,maxSway;
        private static bool previousGesture;

        private sealed class Token
        {
            internal long Id;
            internal InteractionKind Kind;
            internal bool ExpectedCompletion,Cancelled,ReturnRequested;
            internal int Events;
            internal double ActiveMilliseconds,ReturningMilliseconds,SessionBudget;
        }

        public static int Main()
        {
            Stopwatch wall=Stopwatch.StartNew();
            try
            {
                CheckResources();
                foreach(InteractionKind kind in Kinds){Started[kind]=0;Completed[kind]=0;Cancelled[kind]=0;}
                cat=new InteractionController(Seed);shadow=new InteractionController(Seed);
                motion=new IdleMotionController(Seed^0x271828);shadowMotion=new IdleMotionController(Seed^0x271828);
                cat.Completed+=OnCompleted;
                shadow.Completed+=delegate(InteractionKind kind){shadowEvents++;};
                Random random=new Random(Seed^0x314159);
                while(simulated<DayMilliseconds)
                {
                    double roll=random.NextDouble();
                    if(roll<.004)
                    {
                        bool paused=!cat.Paused;cat.Paused=paused;shadow.Paused=paused;motion.Paused=paused;shadowMotion.Paused=paused;pauseToggles++;
                    }
                    if(random.NextDouble()<.03)TryRandomStart(random);
                    if(cat.IsBusy&&random.NextDouble()<.06)ProbeBusy(random);
                    if(random.NextDouble()<.003)CancelActive();
                    if(random.NextDouble()<.002)
                    {
                        int frame=cat.FrameIndex;InteractionKind kind=cat.CurrentKind;
                        Require(!cat.TryStartAction("toy.not-registered"),"unknown action started");unknownCommands++;
                        Require(cat.FrameIndex==frame&&cat.CurrentKind==kind,"unknown action mutated visible state");
                    }
                    if(cat.CurrentKind==InteractionKind.Play)
                    {
                        double x=random.NextDouble()*1.6-.3;
                        if(random.Next(400)==0)x=Double.PositiveInfinity;
                        else if(random.Next(400)==0)x=Double.NegativeInfinity;
                        cat.SetPlayTarget(x);shadow.SetPlayTarget(x);
                    }
                    EndExpiredSession(random);
                    if(random.NextDouble()<.005)ProbeInvalidTime(random);
                    Observe();

                    double delta;
                    double timeRoll=random.NextDouble();
                    if(timeRoll<.0004){delta=60000+random.Next(240001);sleepSteps++;}
                    else if(timeRoll<.001){delta=12000+random.Next(47000);longSteps++;}
                    else if(timeRoll<.015)delta=.25+random.NextDouble()*2;
                    else delta=12+random.Next(141);
                    delta=Math.Min(delta,DayMilliseconds-simulated);
                    Advance(delta);simulated+=delta;ticks++;
                }

                // Resolve the final in-flight token without inventing a cancel
                // at the end of the day, then verify no delayed success leaks.
                cat.Paused=shadow.Paused=motion.Paused=shadowMotion.Paused=false;
                if(active!=null&&(active.Kind==InteractionKind.Pet||active.Kind==InteractionKind.Play)&&!active.ReturnRequested)
                    EndSession(true);
                double drain=0;
                while(cat.IsBusy&&drain<20000){Advance(50);drain+=50;}
                Require(!cat.IsBusy&&active==null,"a controller remained busy after the bounded final drain");
                long eventsBefore=completionEvents;
                cat.Tick(600000);shadow.Tick(600000);
                motion.Tick(600000,false);shadowMotion.Tick(600000,false);
                Require(Neutral(motion.Pose),"inactive ten-minute gap accumulated idle motion");
                for(int i=0;i<100;i++)
                {
                    cat.Tick(10);shadow.Tick(10);motion.Tick(10,true);shadowMotion.Tick(10,true);Observe();
                    Require(Neutral(motion.Pose),"resuming idle replayed queued gestures without quiet space");
                }
                Require(completionEvents==eventsBefore,"a completed/cancelled action emitted a delayed duplicate event");
                Require(SeenFrames.Count==44,"the mixed soak did not exercise all 44 legal frame indices");
                foreach(InteractionKind kind in Kinds)
                {
                    Require(Started[kind]>0&&Completed[kind]>0&&Cancelled[kind]>0,"missing long-run action coverage for "+kind);
                    Require(Started[kind]==Completed[kind]+Cancelled[kind],"unaccounted action token for "+kind);
                }
                Require(CompletedTokens.Count==completionEvents&&completionEvents==shadowEvents,"completion accounting diverged");

                Console.WriteLine("PASS V1 soak: simulatedHours=24 seed="+Seed+" ticks="+ticks+" assertions="+assertions+" frameCoverage="+SeenFrames.Count+"/44");
                foreach(InteractionKind kind in Kinds)
                    Console.WriteLine("ACTION "+kind+" started="+Started[kind]+" completed="+Completed[kind]+" cancelled="+Cancelled[kind]);
                Console.WriteLine("EVENTS completed="+completionEvents+" uniqueTokens="+CompletedTokens.Count+" shadow="+shadowEvents+" busyRejected="+busyProbes+" cancelRequests="+cancelRequests);
                Console.WriteLine("INPUT pauseToggles="+pauseToggles+" invalidDeltaProbes="+invalidProbes+" sleepSteps="+sleepSteps+" longSteps="+longSteps+" unknownCommands="+unknownCommands+" normalSessionEnds="+normalSessionEnds+" cancelledSessionEnds="+cancelledSessionEnds);
                Console.WriteLine("POSE observedGestureStarts="+observedGestures+" maxAbsEarDegrees="+F(maxEar)+" maxTailLift="+F(maxLift)+" maxAbsTailSway="+F(maxSway));
                Console.WriteLine("BOUNDARIES frame=0..43; ear<=6deg; lift=0..1; sway=-1..1; no overlapping idle components; inactive settle<=250ms; final drain="+F(drain)+"ms");
                Console.WriteLine("DESIGN finite actions auto-complete; Pet/Play deliberately hold until End; pause freezes rather than cancels; invalid deltas ignored; idle-motion gaps>=60000ms clear stale motion.");
                Console.WriteLine("SCOPE pure logic plus read-only 44-PNG existence/signature check; no UI, file recycling, settings writes or wall-clock waiting. elapsedWallMs="+wall.ElapsedMilliseconds);
                return 0;
            }
            catch(Exception e){Console.Error.WriteLine("FAIL at simulatedMs="+F(simulated)+" token="+(active==null?"none":active.Id+"/"+active.Kind)+" frame="+(cat==null?-1:cat.FrameIndex)+": "+e);return 1;}
        }

        private static void CheckResources()
        {
            string directory=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..","..","assets","frames"));
            Require(Directory.GetFiles(directory,"baobao-frame-*.png").Length==44,"resource directory does not contain exactly 44 indexed PNG files");
            byte[] signature=new byte[]{137,80,78,71,13,10,26,10};
            for(int frame=0;frame<44;frame++)
            {
                string file=Path.Combine(directory,"baobao-frame-"+frame.ToString("00",CultureInfo.InvariantCulture)+".png");
                Require(File.Exists(file)&&new FileInfo(file).Length>24,"missing/non-PNG frame "+frame);
                using(FileStream stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))
                    for(int i=0;i<signature.Length;i++)Require(stream.ReadByte()==signature[i],"bad PNG signature for frame "+frame);
            }
        }

        private static void TryRandomStart(Random random)
        {
            int command=random.Next(8);
            if(cat.IsBusy){ProbeBusy(random);return;}
            Require(active==null,"new action would orphan an unresolved token");
            bool accepted,reference;
            if(command==6){accepted=cat.Feed();reference=shadow.Feed();}
            else if(command==7)
            {
                int index=random.Next(ActionIds.Length);accepted=cat.TryStartAction(ActionIds[index]);reference=shadow.TryStartAction(ActionIds[index]);
            }
            else{accepted=cat.TryStart(Kinds[command]);reference=shadow.TryStart(Kinds[command]);}
            Require(accepted&&reference&&cat.CurrentKind==shadow.CurrentKind,"idle action start diverged or was rejected");
            InteractionKind kind=cat.CurrentKind;
            active=new Token{Id=++nextToken,Kind=kind,ExpectedCompletion=kind!=InteractionKind.Pet&&kind!=InteractionKind.Play,SessionBudget=200+random.Next(5001)};
            Started[kind]++;busyActiveMilliseconds=0;
        }

        private static void ProbeBusy(Random random)
        {
            Require(cat.IsBusy,"busy probe was invoked while idle");
            int frame=cat.FrameIndex;InteractionKind kind=cat.CurrentKind;bool accepted;
            int command=random.Next(9);
            if(command<6)accepted=cat.TryStart(Kinds[command]);
            else if(command==6)accepted=cat.Feed();
            else if(command==7)accepted=cat.BeginPetting();
            else accepted=cat.BeginPlay();
            Require(!accepted&&cat.FrameIndex==frame&&cat.CurrentKind==kind,"busy input preempted or visibly reset its action");busyProbes++;
            // The shadow intentionally does not receive this rejected input.
        }

        private static void CancelActive()
        {
            cancelRequests++;
            if(active!=null){active.Cancelled=true;active.ExpectedCompletion=false;active.ReturnRequested=true;active.ReturningMilliseconds=0;}
            cat.ReturnToIdle();shadow.ReturnToIdle();CloseResolvedToken();
        }

        private static void EndExpiredSession(Random random)
        {
            if(active==null||active.ReturnRequested||(active.Kind!=InteractionKind.Pet&&active.Kind!=InteractionKind.Play))return;
            if(active.ActiveMilliseconds>=active.SessionBudget)EndSession(random.NextDouble()<.78);
        }

        private static void EndSession(bool success)
        {
            if(active==null||active.ReturnRequested)return;
            active.ReturnRequested=true;active.ReturningMilliseconds=0;
            active.ExpectedCompletion=success;active.Cancelled=!success;
            if(success)normalSessionEnds++;else cancelledSessionEnds++;
            if(active.Kind==InteractionKind.Pet){cat.EndPetting(success);shadow.EndPetting(success);}
            else{cat.EndPlay(success);shadow.EndPlay(success);}
            CloseResolvedToken();
        }

        private static void ProbeInvalidTime(Random random)
        {
            double[] invalid=new double[]{0,-1,Double.NaN,Double.PositiveInfinity,Double.NegativeInfinity,-Double.MaxValue};
            double delta=invalid[random.Next(invalid.Length)];
            int frame=cat.FrameIndex;InteractionKind kind=cat.CurrentKind;IdleMotionPose before=motion.Pose;long events=completionEvents;
            cat.Tick(delta);motion.Tick(delta,!cat.IsBusy);
            Require(cat.FrameIndex==frame&&cat.CurrentKind==kind&&SamePose(before,motion.Pose)&&completionEvents==events,"invalid delta mutated in-flight state");
            invalidProbes++;
            // The shadow intentionally does not receive this invalid delta.
        }

        private static void Advance(double delta)
        {
            bool paused=cat.Paused;
            int previousFrame=cat.FrameIndex;InteractionKind previousKind=cat.CurrentKind;IdleMotionPose previousPose=motion.Pose;long previousEvents=completionEvents;
            if(!paused&&active!=null)
            {
                active.ActiveMilliseconds+=delta;
                if(active.ReturnRequested)active.ReturningMilliseconds+=delta;
            }
            cat.Tick(delta);shadow.Tick(delta);
            motion.Tick(delta,!cat.IsBusy);shadowMotion.Tick(delta,!shadow.IsBusy);
            if(paused)Require(cat.FrameIndex==previousFrame&&cat.CurrentKind==previousKind&&SamePose(previousPose,motion.Pose)&&completionEvents==previousEvents,"pause advanced an action, gesture or completion");
            if(!paused&&cat.IsBusy)busyActiveMilliseconds+=delta;else if(!cat.IsBusy)busyActiveMilliseconds=0;
            if(cat.IsBusy&&!paused&&busyActiveMilliseconds>250)Require(Neutral(motion.Pose),"idle motion persisted/accumulated during interaction");
            if(active!=null&&cat.IsBusy&&!paused)
            {
                bool finite=active.Kind!=InteractionKind.Pet&&active.Kind!=InteractionKind.Play;
                if(finite&&!active.ReturnRequested)Require(active.ActiveMilliseconds<20000,"finite action failed bounded progress");
                if(active.ReturnRequested)Require(active.ReturningMilliseconds<10000,"return-to-idle failed bounded progress");
            }
            Observe();CloseResolvedToken();
        }

        private static void Observe()
        {
            Require(cat.FrameIndex>=0&&cat.FrameIndex<44,"illegal frame index");SeenFrames.Add(cat.FrameIndex);
            Require(cat.FrameIndex==shadow.FrameIndex&&cat.CurrentKind==shadow.CurrentKind&&cat.IsBusy==shadow.IsBusy&&cat.Paused==shadow.Paused,"shadow timeline drift after ignored input");
            Require(completionEvents==shadowEvents,"shadow completion count drift");
            IdleMotionPose p=motion.Pose;
            Require(SamePose(p,shadowMotion.Pose),"shadow idle RNG/phase drift after ignored input");
            Require(Finite(p.LeftEarDegrees)&&Finite(p.RightEarDegrees)&&Finite(p.TailLift)&&Finite(p.TailSway),"nonfinite idle pose");
            Require(Math.Abs(p.LeftEarDegrees)<=6.000000001&&Math.Abs(p.RightEarDegrees)<=6.000000001&&p.TailLift>=0&&p.TailLift<=1.000000001&&Math.Abs(p.TailSway)<=1.000000001,"idle pose exceeded renderer limits");
            int components=(p.LeftEarDegrees!=0?1:0)+(p.RightEarDegrees!=0?1:0)+((p.TailLift!=0||p.TailSway!=0)?1:0);
            Require(components<=1,"multiple idle gestures overlapped");
            bool gesture=components!=0;if(gesture&&!previousGesture)observedGestures++;previousGesture=gesture;
            maxEar=Math.Max(maxEar,Math.Max(Math.Abs(p.LeftEarDegrees),Math.Abs(p.RightEarDegrees)));maxLift=Math.Max(maxLift,p.TailLift);maxSway=Math.Max(maxSway,Math.Abs(p.TailSway));
            Require(cat.IsBusy==(cat.CurrentKind!=InteractionKind.Idle),"busy/kind invariant drift");
            if(cat.CurrentKind==InteractionKind.Idle)Require(cat.FrameIndex<=3,"idle displayed another interaction's frame");
            if(cat.IsBusy)Require(active!=null&&active.Kind==cat.CurrentKind,"active token/kind diverged");
        }

        private static void OnCompleted(InteractionKind kind)
        {
            Require(active!=null&&active.Kind==kind,"completion without matching action token");
            Require(active.ExpectedCompletion&&!active.Cancelled,"cancelled/unfinished session emitted success");
            Require(active.Events==0&&CompletedTokens.Add(active.Id),"duplicate completion for one action token");
            Require(!cat.IsBusy&&cat.CurrentKind==InteractionKind.Idle&&cat.FrameIndex==0,"completion emitted before neutral idle");
            active.Events++;completionEvents++;Completed[kind]++;
        }

        private static void CloseResolvedToken()
        {
            if(active==null||cat.IsBusy)return;
            if(active.Cancelled){Require(active.Events==0,"cancelled token had a success event");Cancelled[active.Kind]++;}
            else Require(active.ExpectedCompletion&&active.Events==1,"normally resolved token omitted its single event");
            active=null;busyActiveMilliseconds=0;
        }

        private static bool SamePose(IdleMotionPose a,IdleMotionPose b)
        {return a.LeftEarDegrees==b.LeftEarDegrees&&a.RightEarDegrees==b.RightEarDegrees&&a.TailLift==b.TailLift&&a.TailSway==b.TailSway;}
        private static bool Neutral(IdleMotionPose p){return p.LeftEarDegrees==0&&p.RightEarDegrees==0&&p.TailLift==0&&p.TailSway==0;}
        private static bool Finite(double n){return!Double.IsNaN(n)&&!Double.IsInfinity(n);}
        private static string F(double n){return n.ToString("0.###",CultureInfo.InvariantCulture);}
        private static void Require(bool condition,string message){assertions++;if(!condition)throw new InvalidOperationException(message);}
    }
}
