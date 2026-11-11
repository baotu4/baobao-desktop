using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using Path = System.IO.Path;

namespace BaobaoDesktop
{
    public static class MainIntegrationTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private static PetWindow window;
        private static InteractionController animation;
        private static string sourceRoot;
        private static string fixtureRoot;
        private static int failures;
        private static readonly List<InteractionKind> completed = new List<InteractionKind>();

        [STAThread]
        public static int Main()
        {
            Application app = null;
            try
            {
                sourceRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", ".."));
                fixtureRoot = Path.Combine(sourceRoot, "main-integration-fixtures", "run-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(fixtureRoot);
                Console.WriteLine("FIXTURE_ROOT=" + fixtureRoot);
                app = new Application(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                MethodInfo closed = typeof(PetWindow).GetMethod("OnClosed", Private);
                Assert(closed != null, "Resolve cleanup before constructing a hidden instance.");
                window = new PetWindow();
                window.Closed -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), window, closed);
                Read<Forms.NotifyIcon>("tray").Visible = false;
                Read<DispatcherTimer>("timer").Stop();
                animation = Read<InteractionController>("animation");
                animation.Completed += delegate(InteractionKind kind) { completed.Add(kind); };
                Run("44 real resources and renderer wiring", ResourcesAndRenderer);
                Run("both PNG toys keep files and start their actual actions", PngToys);
                Run("damaged and mixed toys never fall through to recycle", RejectedToys);
                Run("busy ordinary and toy drops are rejected intact", BusyDrops);
                Run("real recycle feeds once and rejects a busy second batch", RecycleSuccess);
                Run("locked fixture fails without starting an action", RecycleFailure);
                Run("pet completion requires both movement and time", PetMetrics);
                Run("play requires real movement metrics and completes once", PlayMetrics);
                Run("capture loss, hiding and click-through remove play plane", PlayCleanup);
                Run("sleep recovery discards old actions without success", SleepRecovery);
                Run("request bubbles route only to their intended interaction", BubbleRequests);
                Run("pet developer preview is bounded and file-free", DeveloperPreview);
                Run("preview whitelist and executable identity checks", PreviewAndIdentity);
                Run("tray uses native access keys", TrayKeys);
                Console.WriteLine(failures == 0 ? "PASS: 14 hidden Main integration tests" : "FAIL: " + failures + " Main integration tests");
                return failures == 0 ? 0 : 1;
            }
            catch (Exception error) { Console.WriteLine("FAIL setup: " + error); return 1; }
            finally
            {
                if (window != null)
                {
                    if (Read<bool>("processing")) { try { PumpUntilDone(); } catch (Exception) { } }
                    Invoke("CancelInteraction");
                    Set("exiting", true);
                    Read<DispatcherTimer>("timer").Stop();
                    MethodInfo display = typeof(PetWindow).GetMethod("OnDisplayChanged", Private);
                    Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), window, display);
                    Forms.NotifyIcon tray = Read<Forms.NotifyIcon>("tray"); tray.Visible = false; tray.Dispose();
                    Read<Forms.ContextMenuStrip>("trayMenu").Dispose();
                    Drawing.Icon icon = Read<Drawing.Icon>("trayIcon"); if (icon != null) icon.Dispose();
                    window.Close();
                }
                if (app != null) app.Shutdown();
            }
        }

        private static void Run(string name, Action test)
        {
            try
            {
                Invoke("CancelInteraction"); animation.Reset(); Read<IdleMotionController>("idleMotion").Reset();
                Invoke("HideBubble"); Invoke("UpdateVisuals"); completed.Clear();
                Set("lastTick", Read<Stopwatch>("clock").Elapsed.TotalMilliseconds);
                test(); Console.WriteLine("PASS " + name);
            }
            catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error); }
        }

        private static void ResourcesAndRenderer()
        {
            Assert(!window.IsVisible && !window.IsMouseCaptured, "Test must not show or capture the desktop.");
            BitmapSource[] frames = Read<BitmapSource[]>("frames"); IdlePetRenderer renderer = Read<IdlePetRenderer>("cat");
            Assert(frames.Length == 44 && renderer.CleanPlate != null && Object.ReferenceEquals(renderer.NeutralSource, frames[0]), "Resource/neutral/clean-plate wiring missing.");
            Assert(window.Icon != null && Read<Drawing.Icon>("trayIcon") != null, "Portrait tray/window icon was not loaded.");
            using (Stream iconStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("trayicon"))
            using (BinaryReader iconReader = new BinaryReader(iconStream))
            {
                Assert(iconReader.ReadUInt16() == 0 && iconReader.ReadUInt16() == 1, "Tray resource is not an ICO.");
                int iconCount = iconReader.ReadUInt16(); Assert(iconCount == 8, "Tray portrait lost its multi-size resources.");
                HashSet<int> sizes = new HashSet<int>();
                for (int index = 0; index < iconCount; index++)
                {
                    int size = iconReader.ReadByte(); sizes.Add(size == 0 ? 256 : size);
                    iconReader.ReadBytes(15);
                }
                foreach (int size in new int[] { 16, 20, 24, 32, 40, 48, 64, 256 }) Assert(sizes.Contains(size), "Tray portrait is missing size " + size + ".");
            }
            animation.TryStart(InteractionKind.Drink); animation.Tick(500); Invoke("UpdateVisuals");
            Assert(Object.ReferenceEquals(renderer.Source, frames[animation.FrameIndex]) && !renderer.LayeredIdle, "Action frame did not reach renderer.");
            Assert(Object.ReferenceEquals(renderer.NeutralSource, frames[0]), "Action changed neutral micro-motion source.");
            animation.ReturnToIdle(); animation.Tick(3000); Invoke("UpdateVisuals"); Assert(renderer.LayeredIdle, "Idle layering did not recover.");
            IdleMotionController idle = Read<IdleMotionController>("idleMotion");
            for (int i = 0; i < 300 && idle.Pose.TailLift < .01; i++) idle.Tick(20, true);
            Invoke("UpdateVisuals"); Assert(renderer.Pose.TailLift > 0, "Micro-motion pose was not applied.");
            idle.Tick(220, false); Invoke("UpdateVisuals");
            Assert(renderer.Pose.LeftEarDegrees == 0 && renderer.Pose.RightEarDegrees == 0 && renderer.Pose.TailLift == 0 && renderer.Pose.TailSway == 0, "Neutral pose was not restored exactly.");
            Assert(Object.ReferenceEquals(renderer.PetClosedSource, frames[2]), "Continuous pet eyelid source was not wired.");
            animation.BeginPetting(); Invoke("UpdateVisuals");
            Assert(renderer.Petting && renderer.PetBlend == 0 && Object.ReferenceEquals(renderer.NeutralSource, frames[0]), "Pet entry did not use the approved neutral source.");
            animation.Tick(160); Invoke("UpdateVisuals"); Assert(renderer.Petting && renderer.PetBlend > 0 && renderer.PetBlend < 1, "Continuous entry parameters were not applied between business frames.");
            animation.Tick(1040); Invoke("UpdateVisuals");
            Assert(renderer.Pose.LeftEarDegrees == animation.PetLeftEarDegrees && renderer.Pose.RightEarDegrees == animation.PetRightEarDegrees && renderer.Pose.TailLift == animation.PetTailLift && renderer.Pose.TailSway == animation.PetTailSway && renderer.PetBreath == animation.PetBreath, "Pet details were not forwarded instead of idle motion.");
            Assert(renderer.Pose.TailLift > .1 && Math.Abs(renderer.Pose.LeftEarDegrees) > 1 && renderer.PetBreath > .1, "Short-pet detail pose is not visible through Main.");
            double pausedTail = renderer.Pose.TailLift, pausedEar = renderer.Pose.LeftEarDegrees, pausedBreath = renderer.PetBreath;
            animation.Paused = true; animation.Tick(5000); Invoke("UpdateVisuals");
            Assert(renderer.Pose.TailLift == pausedTail && renderer.Pose.LeftEarDegrees == pausedEar && renderer.PetBreath == pausedBreath, "Paused Main advanced a pet detail.");
            animation.Paused = false; animation.EndPetting(false); animation.Tick(250); Invoke("UpdateVisuals");
            Assert(renderer.Petting && Math.Abs(renderer.PetBlend - .5) < .02, "Pet renderer left its neutral-based branch before the return faded.");
            animation.Tick(250); Invoke("UpdateVisuals");
            Assert(!renderer.Petting && renderer.PetBlend == 0 && renderer.PetSway == 0 && renderer.PetBreath == 0 && renderer.Pose.LeftEarDegrees == 0 && renderer.Pose.RightEarDegrees == 0 && renderer.Pose.TailLift == 0 && renderer.Pose.TailSway == 0 && Object.ReferenceEquals(renderer.Source, frames[0]), "Pet exit left a detail offset or a 20/23 business-frame flash.");
        }

        private static void PngToys()
        {
            string[] names = new string[] { "猫抓板.baobao-toy.png", "柱形猫爬架.baobao-toy.png" };
            InteractionKind[] kinds = new InteractionKind[] { InteractionKind.Scratch, InteractionKind.Climb };
            for (int i = 0; i < names.Length; i++)
            {
                string file = CopyFixture(Path.Combine(sourceRoot, "toy-manifests", names[i]), names[i]);
                long length = new FileInfo(file).Length;
                Assert(window.ReceiveDroppedFiles(new string[] { file }, false, true), "PNG toy refused away from bowls.");
                Assert(animation.CurrentKind == kinds[i] && !Read<bool>("processing") && File.Exists(file), "Toy entered recycle or wrong action.");
                animation.Tick(10000); Invoke("UpdateVisuals");
                Assert(!animation.IsBusy && completed.Count == i + 1 && completed[i] == kinds[i] && new FileInfo(file).Length == length, "Toy changed file or completion result.");
            }
        }

        private static void RejectedToys()
        {
            string broken = Fixture("broken.baobao-toy.png", "not a PNG toy");
            string unsupported = Fixture("unknown.baobao-toy.json", "{\"schema\":\"baobao.toy\",\"version\":1,\"kind\":\"unknown\"}");
            string ordinary = Fixture("mixed-ordinary.txt", "Mixed batch must remain.");
            string toy = CopyFixture(Path.Combine(sourceRoot, "toy-manifests", "猫抓板.baobao-toy.png"), "mixed.baobao-toy.png");
            Assert(!window.ReceiveDroppedFiles(new string[] { broken }, true, true), "Damaged PNG accepted.");
            window.RecycleDroppedFiles(new string[] { broken });
            Assert(!window.ReceiveDroppedFiles(new string[] { unsupported }, true, true), "Unknown toy accepted.");
            Assert(!window.ReceiveDroppedFiles(new string[] { toy, ordinary }, true, true), "Mixed batch accepted.");
            foreach (string file in new string[] { broken, unsupported, ordinary, toy }) Assert(File.Exists(file), "Rejected file disappeared.");
            Assert(!Read<bool>("processing") && !animation.IsBusy, "Rejected toy started work.");
        }

        private static void BusyDrops()
        {
            string ordinary = Fixture("busy-ordinary.txt", "Busy must not recycle.");
            string toy = CopyFixture(Path.Combine(sourceRoot, "toy-manifests", "猫抓板.baobao-toy.png"), "busy.baobao-toy.png");
            animation.TryStart(InteractionKind.Scratch);
            Assert(!window.ReceiveDroppedFiles(new string[] { ordinary }, true, true) && !window.ReceiveDroppedFiles(new string[] { toy }, true, true), "Busy action accepted another batch.");
            Assert(File.Exists(ordinary) && File.Exists(toy) && !Read<bool>("processing"), "Busy drop mutated files.");
        }

        private static void RecycleSuccess()
        {
            string first = Fixture("recycle-success.txt", "Only this isolated integration fixture is recycled.");
            string second = Fixture("recycle-busy.txt", "Second batch must remain.");
            Assert(window.ReceiveDroppedFiles(new string[] { first }, true, false), "Valid ordinary batch refused.");
            Assert(Read<bool>("processing") && !window.ReceiveDroppedFiles(new string[] { second }, true, true), "Processing guard failed.");
            PumpUntilDone();
            Assert(!File.Exists(first) && File.Exists(second), "Recycle/busy file paths are wrong.");
            Assert(animation.CurrentKind == InteractionKind.Eat || animation.CurrentKind == InteractionKind.Drink, "Recycle did not select Eat or Drink.");
            Assert(Read<TextBlock>("bubbleText").Text == "已回收 1 个文件。" && completed.Count == 0, "Recycle fact/early completion incorrect.");
            animation.Tick(10000);
            string message = Read<TextBlock>("bubbleText").Text;
            Assert(completed.Count == 1 && message.StartsWith("已回收 1 个文件。\n", StringComparison.Ordinal), "Unified completion lost the recycle fact.");
            animation.Tick(10000); Assert(completed.Count == 1 && Read<TextBlock>("bubbleText").Text == message, "Completion repeated.");
        }

        private static void RecycleFailure()
        {
            string file = Fixture("locked-must-remain.txt", "Exclusive fixture remains intact.");
            using (FileStream held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert(window.ReceiveDroppedFiles(new string[] { file }, true, true), "Locked fixture did not reach the real service.");
                PumpUntilDone();
                Assert(File.Exists(file) && !animation.IsBusy && completed.Count == 0, "Failed recycle animated or deleted.");
                Assert(Read<TextBlock>("bubbleText").Text.Contains("占用"), "Failure bubble did not describe the lock.");
            }
        }

        private static void PetMetrics()
        {
            animation.BeginPetting(); Set("petStarted", true); Set("petPending", false);
            Set("petLastPoint", new Point(130, 160)); Set("petLastMoveAt", 0d);
            Invoke("TrackPetPoint", new Point(160, 160), 150d);
            Assert(!(bool)Invoke("HasCompletedPetting"), "Distance alone completed petting.");
            Set("petDistance", 0d); Set("petMovingMilliseconds", 0d);
            Set("petLastPoint", new Point(130, 160)); Set("petLastMoveAt", 0d);
            for (int i = 1; i <= 12; i++) Invoke("TrackPetPoint", new Point(130 + (i % 2 == 0 ? 0 : 1), 160), i * 100d);
            Assert(!(bool)Invoke("HasCompletedPetting"), "Time alone completed petting.");
            for (int i = 13; i <= 25; i++) Invoke("TrackPetPoint", new Point(130 + (i % 2 == 0 ? 0 : 4), 160), i * 100d);
            Assert((bool)Invoke("HasCompletedPetting"), "Valid moving petting did not qualify.");
            Invoke("FinishPettingGesture", true); Assert(!Read<bool>("petStarted") && !window.IsMouseCaptured && completed.Count == 0, "Gesture ended with stale capture or early feedback.");
            animation.Tick(1000); Invoke("UpdateVisuals");
            Assert(completed.Count == 1 && completed[0] == InteractionKind.Pet && !Read<IdlePetRenderer>("cat").Petting && Read<IdlePetRenderer>("cat").PetBlend == 0, "Pet success did not use unified completion and clear its continuous pose.");
        }

        private static void PlayMetrics()
        {
            Assert((bool)Invoke("BeginPlaySession"), "Play start refused.");
            Assert(Read<Rectangle>("playReceiver").Visibility == Visibility.Visible && ((SolidColorBrush)Read<Rectangle>("playReceiver").Fill).Color.A == 1 && !window.IsMouseCaptured, "Play receiver/capture policy wrong.");
            double time = Read<double>("playLastMoveAt"); Point start = Read<Point>("playLastPoint");
            Invoke("TrackPlayPoint", start, time + 10000);
            Assert(Read<double>("playMovingMilliseconds") == 0 && Read<bool>("playSession"), "Stationary hold counted as play movement.");
            time += 10000;
            for (int i = 1; i <= 34; i++) Invoke("TrackPlayPoint", new Point(start.X + (i % 2 == 0 ? 0 : 5), start.Y), time + i * 100);
            Assert(Read<bool>("playSession") && completed.Count == 0, "Play still ended after only about three seconds of movement.");
            for (int i = 35; i <= 64; i++) Invoke("TrackPlayPoint", new Point(start.X + (i % 2 == 0 ? 0 : 5), start.Y), time + i * 100);
            Assert(!Read<bool>("playSession") && Read<Rectangle>("playReceiver").Visibility == Visibility.Collapsed && completed.Count == 0, "Qualified play failed to remove the plane before returning.");
            animation.Tick(1000); Assert(completed.Count == 1 && completed[0] == InteractionKind.Play, "Play did not complete once after return.");
            Assert((bool)Invoke("BeginPlaySession"), "Distance-gate check could not start.");
            time = Read<double>("playLastMoveAt"); start = Read<Point>("playLastPoint");
            for (int i = 1; i <= 64; i++) Invoke("TrackPlayPoint", new Point(start.X + (i % 2 == 0 ? 0 : 1), start.Y), time + i * 100);
            Assert(Read<bool>("playSession") && completed.Count == 1, "Moving time alone bypassed the 180px movement requirement.");
            Invoke("CancelInteraction"); animation.Tick(1000); Assert(completed.Count == 1, "Cancelled short-distance play emitted success.");
        }

        private static void PlayCleanup()
        {
            string[] exits = new string[] { "OnLostMouseCapture", "HidePet", "SetClickThrough" };
            foreach (string exit in exits)
            {
                Invoke("CancelInteraction"); animation.Reset();
                Assert((bool)Invoke("BeginPlaySession"), "Cleanup case could not start.");
                if (exit == "OnLostMouseCapture") Invoke(exit, window, null);
                else if (exit == "SetClickThrough") Invoke(exit, true);
                else Invoke(exit);
                Assert(!Read<bool>("playSession") && Read<Rectangle>("playReceiver").Visibility == Visibility.Collapsed && Read<Ellipse>("playDot").Visibility == Visibility.Collapsed && !window.IsMouseCaptured, "Exit left an input plane or capture: " + exit);
                animation.Tick(1000); Assert(completed.Count == 0, "Cancelled cleanup emitted success.");
                if (exit == "SetClickThrough") Invoke(exit, false);
            }
            Invoke("CancelInteraction"); animation.Reset(); Invoke("BeginPlaySession");
            double now = Read<Stopwatch>("clock").Elapsed.TotalMilliseconds;
            Set("playStartedAt", now - 23000); Set("lastTick", now); Invoke("OnTick", window, EventArgs.Empty);
            Assert(Read<bool>("playSession"), "Play timed out before its 24-second allowance.");
            now = Read<Stopwatch>("clock").Elapsed.TotalMilliseconds;
            Set("playStartedAt", now - 24001); Set("lastTick", now); Invoke("OnTick", window, EventArgs.Empty);
            Assert(!Read<bool>("playSession") && Read<Rectangle>("playReceiver").Visibility == Visibility.Collapsed, "Play timeout did not remove its local input plane.");
            animation.Tick(1000); Assert(completed.Count == 0, "Timed-out play was reported as a success.");
        }

        private static void SleepRecovery()
        {
            Invoke("BeginPlaySession"); animation.Paused = true;
            double now = Read<Stopwatch>("clock").Elapsed.TotalMilliseconds;
            Set("lastTick", now - 5000); Set("actionStartedAt", now - 10000);
            Invoke("OnTick", window, EventArgs.Empty);
            Assert(!Read<bool>("playSession") && !animation.IsBusy && animation.Paused && completed.Count == 0, "Sleep replayed or completed an old action.");
            Assert(Read<Rectangle>("playReceiver").Visibility == Visibility.Collapsed, "Sleep left the play plane.");
        }

        private static void BubbleRequests()
        {
            Type actionType = typeof(PetWindow).GetNestedType("BubbleAction", BindingFlags.NonPublic);
            foreach (string name in new string[] { "Food", "Water", "Pet" })
            {
                Invoke("TriggerBubbleAction", Enum.Parse(actionType, name));
                Assert(!animation.IsBusy && completed.Count == 0, "Request falsely fed or petted automatically.");
            }
            Assert((Read<DateTime>("nextRemark") - DateTime.UtcNow).TotalMinutes >= 8.9, "Interaction did not defer automatic requests.");
            Invoke("TriggerBubbleAction", Enum.Parse(actionType, "Play"));
            Assert(Read<bool>("playSession") && animation.CurrentKind == InteractionKind.Play, "Play request did not enter play.");
        }

        private static void DeveloperPreview()
        {
            const double durationToleranceMilliseconds = .000001;
            int roundedSamples = 0;
            double maximumDurationError = 0;
            for (int attempt = 0; attempt < 32; attempt++)
            {
                window.StartDeveloperPreview("pet");
                InteractionKind actualKind = animation.CurrentKind;
                bool captured = window.IsMouseCaptured;
                double started = Read<double>("actionStartedAt");
                double deadline = Read<double>("previewPetUntil");
                double duration = deadline - started;
                double error = Math.Abs(duration - 2000);
                if (error > 0) roundedSamples++;
                maximumDurationError = Math.Max(maximumDurationError, error);
                if (attempt == 0 || (error > 0 && roundedSamples == 1) || error > durationToleranceMilliseconds)
                    Console.WriteLine(String.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "PREVIEW_DIAGNOSTIC attempt={0} kind={1} captured={2} started={3:R} deadline={4:R} duration={5:R}",
                        attempt, actualKind, captured, started, deadline, duration));
                Assert(actualKind == InteractionKind.Pet, "Pet preview started wrong kind: " + actualKind);
                Assert(!captured, "Pet preview captured mouse input.");
                // Adding 2000 to a Stopwatch-derived double and subtracting the
                // original value can round by an ulp. A one-nanosecond tolerance
                // accepts arithmetic noise while still checking a bounded 2s demo.
                Assert(error <= durationToleranceMilliseconds, "Pet preview deadline differs: " + duration.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            }
            Console.WriteLine(String.Format(System.Globalization.CultureInfo.InvariantCulture,
                "PREVIEW_BOUNDARY_CHECK samples=32 rounded={0} maxErrorMs={1:R} toleranceMs={2:R}",
                roundedSamples, maximumDurationError, durationToleranceMilliseconds));
            double now = Read<Stopwatch>("clock").Elapsed.TotalMilliseconds;
            Set("previewPetUntil", now - 1); Set("lastTick", now);
            Invoke("OnTick", window, EventArgs.Empty);
            animation.Tick(1000); Assert(!animation.IsBusy && completed.Count == 1 && completed[0] == InteractionKind.Pet, "Developer preview did not finish its bounded demo.");
            Assert(!Read<bool>("processing"), "Developer preview invoked file processing.");
        }

        private static void PreviewAndIdentity()
        {
            MethodInfo parse = typeof(Program).GetMethod("PreviewCode", BindingFlags.Static | BindingFlags.NonPublic);
            Assert((int)parse.Invoke(null, new object[] { new string[] { "--preview=pet" } }) == 4, "Pet argument mapping wrong.");
            Assert((int)parse.Invoke(null, new object[] { new string[] { "--preview=files.recycle" } }) == 0, "Preview accepted a file operation.");
            Assert(Program.PreviewAction(5) == "toy.scratcher" && Program.PreviewAction(6) == "toy.cat_tree" && Program.PreviewAction(99) == null, "IPC action whitelist wrong.");
            Version version;
            string product = Path.Combine(sourceRoot, "bin", "Baobao.exe");
            Assert(File.Exists(product), "This repository's bin/Baobao.exe is missing; run Run-Tests.ps1 -NativeChecks to build it first.");
            Version expectedVersion = Assembly.GetExecutingAssembly().GetName().Version;
            Assert(Program.IsKnownBaobaoExecutable(product, out version) && version == expectedVersion, "Known repository-built product identity failed.");
            Assert(!Program.IsKnownBaobaoExecutable(Assembly.GetExecutingAssembly().Location, out version), "Different test executable falsely authenticated as Baobao.exe.");
        }

        private static void TrayKeys()
        {
            Invoke("BuildTrayMenu"); Forms.ContextMenuStrip menu = Read<Forms.ContextMenuStrip>("trayMenu");
            bool play = false, end = false;
            foreach (Forms.ToolStripItem item in menu.Items) { if (item.Text.Contains("&P")) play = true; if (item.Text.Contains("&E")) end = true; }
            Assert(play && end, "Native tray access keys missing.");
        }

        private static string Fixture(string name, string value)
        {
            string path = FixturePath(name); File.WriteAllText(path, value, new UTF8Encoding(false)); return path;
        }
        private static string CopyFixture(string source, string name) { string path = FixturePath(name); File.Copy(source, path, false); return path; }
        private static string FixturePath(string name)
        {
            Assert(name == Path.GetFileName(name), "Fixture name escaped root.");
            string path = Path.GetFullPath(Path.Combine(fixtureRoot, name));
            Assert(path.StartsWith(fixtureRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Fixture path escaped root."); return path;
        }
        private static void PumpUntilDone()
        {
            Stopwatch watch = Stopwatch.StartNew(); DispatcherFrame frame = new DispatcherFrame();
            DispatcherTimer poll = new DispatcherTimer(DispatcherPriority.Background); poll.Interval = TimeSpan.FromMilliseconds(10);
            poll.Tick += delegate { if (!Read<bool>("processing") || watch.ElapsedMilliseconds > 20000) frame.Continue = false; };
            poll.Start(); try { Dispatcher.PushFrame(frame); } finally { poll.Stop(); }
            Assert(!Read<bool>("processing"), "Recycle worker timed out.");
        }
        private static T Read<T>(string name) { return (T)typeof(PetWindow).GetField(name, Private).GetValue(window); }
        private static void Set(string name, object value) { typeof(PetWindow).GetField(name, Private).SetValue(window, value); }
        private static object Invoke(string name, params object[] args)
        {
            foreach (MethodInfo method in typeof(PetWindow).GetMethods(Private))
                if (method.Name == name && method.GetParameters().Length == args.Length) return method.Invoke(window, args);
            throw new MissingMethodException(name);
        }
        private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
