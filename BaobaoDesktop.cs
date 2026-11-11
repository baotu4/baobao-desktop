using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Xml;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using Path = System.IO.Path;

namespace BaobaoDesktop
{
    public sealed class PetSettings
    {
        public double Size = 300;
        public double Left = Double.NaN;
        public double Top = Double.NaN;
        public bool Topmost = true;
        public bool Quiet;
        public static string Folder { get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BaobaoDesktop"); } }
        public static PetSettings Load()
        {
            PetSettings s = new PetSettings();
            try
            {
                XmlDocument doc = new XmlDocument();
                doc.XmlResolver = null;
                XmlReaderSettings readerSettings = new XmlReaderSettings();
                readerSettings.DtdProcessing = DtdProcessing.Prohibit;
                using (XmlReader reader = XmlReader.Create(System.IO.Path.Combine(Folder, "settings.xml"), readerSettings)) doc.Load(reader);
                XmlElement root = doc.DocumentElement;
                double number;
                if (Double.TryParse(root.GetAttribute("size"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number) && !Double.IsNaN(number) && !Double.IsInfinity(number)) s.Size = Math.Max(220, Math.Min(420, number));
                if (Double.TryParse(root.GetAttribute("left"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number) && !Double.IsInfinity(number)) s.Left = number;
                if (Double.TryParse(root.GetAttribute("top"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number) && !Double.IsInfinity(number)) s.Top = number;
                s.Topmost = root.GetAttribute("topmost") != "false";
                s.Quiet = root.GetAttribute("quiet") == "true";
            }
            catch (Exception ex) { if (!(ex is IOException) && !(ex is UnauthorizedAccessException) && !(ex is XmlException)) throw; }
            return s;
        }
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                XmlWriterSettings options = new XmlWriterSettings(); options.Indent = true;
                using (XmlWriter writer = XmlWriter.Create(System.IO.Path.Combine(Folder, "settings.xml"), options))
                {
                    writer.WriteStartElement("baobao");
                    writer.WriteAttributeString("size", Size.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("left", Left.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("top", Top.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("topmost", Topmost ? "true" : "false");
                    writer.WriteAttributeString("quiet", Quiet ? "true" : "false");
                    writer.WriteEndElement();
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public sealed class PetWindow : Window
    {
        private const double BubbleSpace = 66;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x80;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WM_SHOW_BAOBAO = 0x8000 + 42;
        private const int WM_PREVIEW_BAOBAO = 0x8000 + 43;
        private readonly PetSettings settings;
        private readonly InteractionController animation = new InteractionController();
        private readonly IdleMotionController idleMotion = new IdleMotionController();
        private readonly DropRouter dropRouter = new DropRouter();
        private readonly BitmapSource[] frames = new BitmapSource[44];
        private readonly Canvas stage = new Canvas();
        private readonly IdlePetRenderer cat = new IdlePetRenderer();
        private readonly Rectangle playReceiver = new Rectangle();
        private readonly Ellipse playDot = new Ellipse();
        private readonly Border bubble = new Border();
        private readonly TextBlock bubbleText = new TextBlock();
        private readonly Ellipse foodGlow = new Ellipse();
        private readonly DispatcherTimer timer = new DispatcherTimer();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Random random = new Random();
        private readonly Forms.NotifyIcon tray = new Forms.NotifyIcon();
        private readonly Forms.ContextMenuStrip trayMenu = new Forms.ContextMenuStrip();
        private Drawing.Icon trayIcon;
        private HwndSource source;
        private IntPtr handle;
        private double lastTick;
        private DateTime bubbleUntil = DateTime.MinValue;
        private DateTime nextRemark = DateTime.UtcNow.AddMinutes(10);
        private bool processing;
        private bool clickThrough;
        private bool exiting;
        private int shownFrame = -1;
        private string dragReason;
        private IdleMotionPose shownPose;
        private bool shownPetting;
        private double shownPetBlend;
        private double shownPetSway;
        private double shownPetBreath;
        private enum BubbleAction { None, Food, Water, Pet, Play }
        private BubbleAction bubbleAction;
        private bool petPending;
        private bool petStarted;
        private double petPressedAt;
        private Point petLastPoint;
        private double petLastMoveAt;
        private double petMovingMilliseconds;
        private double petDistance;
        private bool playSession;
        private double playStartedAt;
        private Point playLastPoint;
        private double playLastMoveAt;
        private double playMovingMilliseconds;
        private double playDistance;
        private double actionStartedAt;
        private double previewPetUntil;
        private string recycleOutcome;

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public int Flags; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref NativeRect rect, int flags);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        public PetWindow()
        {
            settings = PetSettings.Load();
            Title = "包包桌面摆件";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
#if REVIEW
            ShowInTaskbar = true;
#else
            ShowInTaskbar = false;
#endif
            ShowActivated = false;
            Topmost = settings.Topmost;
            Width = settings.Size; Height = settings.Size + BubbleSpace;
            MinWidth = 220; MaxWidth = 420;
            LoadFrames();
            Content = stage;
            playReceiver.Fill = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255));
            playReceiver.Visibility = Visibility.Collapsed;
            stage.Children.Add(playReceiver);
            stage.Children.Add(cat);
            cat.SnapsToDevicePixels = true;
            RenderOptions.SetBitmapScalingMode(cat, BitmapScalingMode.HighQuality);
            Canvas.SetTop(cat, BubbleSpace);
            foodGlow.Fill = new SolidColorBrush(Color.FromArgb(32, 100, 170, 125));
            foodGlow.Stroke = new SolidColorBrush(Color.FromRgb(107, 163, 120));
            foodGlow.StrokeThickness = 2;
            foodGlow.IsHitTestVisible = false;
            foodGlow.Visibility = Visibility.Collapsed;
            stage.Children.Add(foodGlow);
            bubble.Background = new SolidColorBrush(Color.FromArgb(218, 249, 246, 238));
            bubble.BorderBrush = new SolidColorBrush(Color.FromArgb(108, 151, 139, 121));
            bubble.BorderThickness = new Thickness(.75);
            bubble.CornerRadius = new CornerRadius(10);
            bubble.Padding = new Thickness(8, 5, 8, 5);
            bubble.Visibility = Visibility.Collapsed;
            bubble.IsHitTestVisible = false;
            bubbleText.FontFamily = new FontFamily("Microsoft YaHei");
            bubbleText.FontSize = 11.5;
            bubbleText.Foreground = new SolidColorBrush(Color.FromRgb(61, 53, 44));
            bubbleText.TextWrapping = TextWrapping.Wrap;
            bubbleText.TextTrimming = TextTrimming.CharacterEllipsis;
            bubbleText.LineHeight = 16;
            bubbleText.MaxHeight = 32;
            bubble.Child = bubbleText;
            stage.Children.Add(bubble);
            bubble.MouseLeftButtonDown += OnBubbleClick;
            playDot.Width = playDot.Height = 8;
            playDot.Fill = new SolidColorBrush(Color.FromArgb(235, 235, 207, 139));
            playDot.Stroke = new SolidColorBrush(Color.FromArgb(190, 255, 251, 227));
            playDot.StrokeThickness = 1;
            playDot.IsHitTestVisible = false;
            playDot.Visibility = Visibility.Collapsed;
            stage.Children.Add(playDot);
            LayoutPet();
            animation.Completed += OnInteractionCompleted;
            AllowDrop = true;
            PreviewDragEnter += OnDragOver;
            PreviewDragOver += OnDragOver;
            PreviewDragLeave += delegate { HideDragHint(); };
            PreviewDrop += OnDrop;
            MouseLeftButtonDown += OnLeftDown;
            MouseLeftButtonUp += OnLeftUp;
            MouseMove += OnMouseMove;
            MouseLeave += OnMouseLeave;
            LostMouseCapture += OnLostMouseCapture;
            PreviewKeyDown += OnPreviewKeyDown;
            IsVisibleChanged += delegate
            {
                if (!IsVisible)
                {
                    CancelInteraction();
#if REVIEW
                    ReviewEvents.Write("window.hidden", animation.CurrentKind, true, 0, 0);
#endif
                }
            };
            MouseRightButtonUp += delegate(object sender, MouseButtonEventArgs e) { FinishPettingGesture(false); OpenMenu(); e.Handled = true; };
            MouseWheel += delegate(object sender, MouseWheelEventArgs e) { ResizePet(Width + (e.Delta > 0 ? 20 : -20)); e.Handled = true; };
            SourceInitialized += OnSourceInitialized;
            Loaded += delegate { PositionPet(false); idleMotion.Reset(); idleMotion.Paused = animation.Paused; MarkInteraction(); ShowBubble("我是包包。长按轻移摸摸，Alt+拖动换位置。", 5); };
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { if (processing && !exiting) { e.Cancel = true; ShowBubble("正在回收文件，请稍等一下。", 4); } };
            Closed += OnClosed;
            CreateTray();
            timer.Interval = TimeSpan.FromMilliseconds(40);
            timer.Tick += OnTick;
            timer.Start();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            MarkInteraction();
        }

        private void LoadFrames()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            for (int index = 0; index < frames.Length; index++)
            {
                frames[index] = ReadBitmap(assembly, "frame" + index.ToString("00"));
                if (Math.Abs((double)frames[index].PixelWidth / frames[index].PixelHeight - 1) > .01)
                    throw new InvalidDataException("包包的动作素材需要使用统一的正方形画布。");
            }
            cat.NeutralSource = frames[0];
            cat.CleanPlate = ReadBitmap(assembly, "cleanplate");
            cat.PetClosedSource = frames[2];
            cat.Source = frames[0];
            cat.LayeredIdle = true;
            shownFrame = 0;
        }

        private static BitmapSource ReadBitmap(Assembly assembly, string name)
        {
            using (Stream stream = assembly.GetManifestResourceStream(name))
            {
                if (stream == null) throw new InvalidDataException("缺少包包的动作素材：" + name);
                BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                FormatConvertedBitmap decoded = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
                int width = decoded.PixelWidth, height = decoded.PixelHeight, stride = width * 4;
                byte[] pixels = new byte[stride * height];
                decoded.CopyPixels(pixels, stride, 0);
                for (int p = 0; p < pixels.Length; p += 4) if (pixels[p + 3] < 12) { pixels[p] = 0; pixels[p + 1] = 0; pixels[p + 2] = 0; pixels[p + 3] = 0; }
                BitmapSource frame = BitmapSource.Create(width, height, decoded.DpiX, decoded.DpiY, PixelFormats.Bgra32, null, pixels, stride);
                frame.Freeze(); return frame;
            }
        }

        private void LayoutPet()
        {
            cat.Width = Width; cat.Height = Width;
            playReceiver.Width = Width; playReceiver.Height = Width;
            Canvas.SetTop(playReceiver, BubbleSpace);
            Height = Width + BubbleSpace;
            foodGlow.Width = Width * .39; foodGlow.Height = Width * .18;
            Canvas.SetLeft(foodGlow, 0); Canvas.SetTop(foodGlow, BubbleSpace + Width * .75);
            bubble.Width = Math.Min(Width - 28, 228);
            Canvas.SetLeft(bubble, 14); Canvas.SetTop(bubble, 11);
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            handle = new WindowInteropHelper(this).Handle;
            source = HwndSource.FromHwnd(handle);
            source.AddHook(WindowHook);
#if !REVIEW
            SetWindowLong(handle, GWL_EXSTYLE, GetWindowLong(handle, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
#endif
        }
        private IntPtr WindowHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_SHOW_BAOBAO) { ShowPet(); handled = true; }
            else if (msg == WM_PREVIEW_BAOBAO) { StartDeveloperPreview(Program.PreviewAction(wParam.ToInt32())); handled = true; }
            return IntPtr.Zero;
        }
        private void OnDisplayChanged(object sender, EventArgs e)
        {
            if (exiting || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(new Action(delegate { if (!exiting && !Dispatcher.HasShutdownStarted) PositionPet(false); }));
        }
        private void EnsureOnScreen()
        {
            NativeRect actual;
            if (handle == IntPtr.Zero || !GetWindowRect(handle, out actual)) return;
            MonitorInfo info = new MonitorInfo(); info.Size = Marshal.SizeOf(typeof(MonitorInfo));
            if (!GetMonitorInfo(MonitorFromRect(ref actual, 2), ref info)) return;
            int x = Math.Max(info.Work.Left, Math.Min(info.Work.Right - (actual.Right - actual.Left), actual.Left));
            int y = Math.Max(info.Work.Top, Math.Min(info.Work.Bottom - (actual.Bottom - actual.Top), actual.Top));
            if (x != actual.Left || y != actual.Top)
            {
                Point local = PointFromScreen(new Point(x, y));
                Left += local.X; Top += local.Y;
            }
        }
        private void PositionPet(bool reset)
        {
            double left = reset ? Double.NaN : settings.Left;
            double top = reset ? Double.NaN : settings.Top;
            double screenLeft = SystemParameters.VirtualScreenLeft, screenTop = SystemParameters.VirtualScreenTop;
            double screenRight = screenLeft + SystemParameters.VirtualScreenWidth, screenBottom = screenTop + SystemParameters.VirtualScreenHeight;
            if (Double.IsNaN(left) || Double.IsNaN(top) || left + Width < screenLeft + 40 || left > screenRight - 40 || top + Height < screenTop + 40 || top > screenBottom - 40)
            {
                Rect work = SystemParameters.WorkArea;
                left = work.Right - Width - 34;
                top = work.Bottom - Height - 16;
            }
            Left = Math.Max(screenLeft, Math.Min(screenRight - Width, left));
            Top = Math.Max(screenTop, Math.Min(screenBottom - Height, top));
            EnsureOnScreen();
            Remember();
        }
        private void Remember() { settings.Left = Left; settings.Top = Top; settings.Size = Width; settings.Topmost = Topmost; settings.Save(); }
        private void ResizePet(double size)
        {
            if (petPending || petStarted || playSession) CancelInteraction();
            double old = Width;
            Width = Math.Max(220, Math.Min(420, size));
            Left -= (Width - old) / 2;
            Top -= Width - old;
            LayoutPet(); Remember(); PositionPet(false); MarkInteraction();
        }
        private void ShowPet()
        {
            if (clickThrough) SetClickThrough(false);
            Show();
            PositionPet(false);
            MarkInteraction();
            ShowBubble("包包在这里。", 3);
#if REVIEW
            ReviewEvents.Write("window.shown", animation.CurrentKind, true, 0, 0);
#endif
        }
        private void SetClickThrough(bool value)
        {
            if (value) CancelInteraction();
            clickThrough = value;
            if (handle != IntPtr.Zero)
            {
                int style = GetWindowLong(handle, GWL_EXSTYLE);
                SetWindowLong(handle, GWL_EXSTYLE, value ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT);
            }
            MarkInteraction();
            if (value) ShowBubble("穿透已开启，可从托盘恢复。", 5);
#if REVIEW
            ReviewEvents.Write(value ? "input.passthrough.on" : "input.passthrough.off", animation.CurrentKind, true, 0, 0);
#endif
        }

        private void OnTick(object sender, EventArgs e)
        {
            double now = clock.Elapsed.TotalMilliseconds;
            double previousTick = lastTick;
            double delta = Math.Max(0, now - previousTick); lastTick = now;
            if (delta > 2000)
            {
                bool paused = animation.Paused;
                if (actionStartedAt <= previousTick)
                {
                    CancelInteraction(); animation.Reset(); animation.Paused = paused;
                }
                else if (petPending && petPressedAt <= previousTick) FinishPettingGesture(false);
                idleMotion.Reset(); idleMotion.Paused = paused;
                nextRemark = DateTime.UtcNow.AddMinutes(10);
                delta = 0;
            }
            if (petPending)
            {
                if (Mouse.LeftButton != MouseButtonState.Pressed || !IsPetArea(Mouse.GetPosition(stage)) || animation.Paused || processing)
                    FinishPettingGesture(false);
                else if (now - petPressedAt >= 400 && !animation.IsBusy && animation.BeginPetting())
                {
                    petPending = false; petStarted = true;
                    petMovingMilliseconds = 0; petDistance = 0; petLastMoveAt = now;
                    actionStartedAt = now; MarkInteraction();
#if REVIEW
                    ReviewEvents.Write("pet.begin", InteractionKind.Pet, true, 0, 0);
#endif
                }
            }
            if (playSession && now - playStartedAt >= 24000) FinishPlaySession(false);
            if (previewPetUntil > 0 && now >= previewPetUntil)
            {
                previewPetUntil = 0; animation.EndPetting(true);
            }
            if (IsVisible) animation.Tick(Math.Min(delta, 150));
            idleMotion.Paused = animation.Paused;
            idleMotion.Tick(delta, IsVisible && !processing && !animation.IsBusy);
            UpdateVisuals();
            if (bubble.Visibility == Visibility.Visible && DateTime.UtcNow >= bubbleUntil) HideBubble();
            if (DateTime.UtcNow >= nextRemark)
            {
                if (IsVisible && !settings.Quiet && !processing && !animation.IsBusy && !animation.Paused && !petPending)
                {
                    int request = random.Next(4);
                    string[] lines = { "想吃点东西，喵。", "想喝一点水。", "可以摸摸我的头吗？", "陪我追一会儿小光点吧。" };
                    BubbleAction[] actions = { BubbleAction.Food, BubbleAction.Water, BubbleAction.Pet, BubbleAction.Play };
                    ShowBubble(lines[request], 5, actions[request]);
                    MarkInteraction();
                }
                else nextRemark = DateTime.UtcNow.AddMinutes(1);
            }
        }

        private void UpdateVisuals()
        {
            if (shownFrame != animation.FrameIndex)
            {
                shownFrame = animation.FrameIndex;
                cat.Source = frames[shownFrame];
                cat.LayeredIdle = shownFrame >= 0 && shownFrame <= 3;
            }
            bool petting = animation.CurrentKind == InteractionKind.Pet;
            IdleMotionPose pose = petting ? new IdleMotionPose
            {
                LeftEarDegrees = animation.PetLeftEarDegrees,
                RightEarDegrees = animation.PetRightEarDegrees,
                TailLift = animation.PetTailLift,
                TailSway = animation.PetTailSway
            } : idleMotion.Pose;
            bool exactNeutral = pose.LeftEarDegrees == 0 && pose.RightEarDegrees == 0 && pose.TailLift == 0 && pose.TailSway == 0;
            bool wasNonneutral = shownPose.LeftEarDegrees != 0 || shownPose.RightEarDegrees != 0 || shownPose.TailLift != 0 || shownPose.TailSway != 0;
            if ((exactNeutral && wasNonneutral) || Math.Abs(pose.LeftEarDegrees - shownPose.LeftEarDegrees) > .0001 || Math.Abs(pose.RightEarDegrees - shownPose.RightEarDegrees) > .0001 ||
                Math.Abs(pose.TailLift - shownPose.TailLift) > .00001 || Math.Abs(pose.TailSway - shownPose.TailSway) > .00001)
            {
                cat.Pose = pose; shownPose = pose;
            }
            if (shownPetting != petting) { cat.Petting = petting; shownPetting = petting; }
            if (shownPetBlend != animation.PetBlend) { cat.PetBlend = animation.PetBlend; shownPetBlend = animation.PetBlend; }
            if (shownPetSway != animation.PetSway) { cat.PetSway = animation.PetSway; shownPetSway = animation.PetSway; }
            if (shownPetBreath != animation.PetBreath) { cat.PetBreath = animation.PetBreath; shownPetBreath = animation.PetBreath; }
        }

        private void MarkInteraction() { nextRemark = DateTime.UtcNow.AddSeconds(540 + random.Next(121)); }

        private void OnInteractionCompleted(InteractionKind kind)
        {
            string[] lines;
            switch (kind)
            {
                case InteractionKind.Eat: lines = new string[] { "吃好啦，喵。", "这顿很合胃口。" }; break;
                case InteractionKind.Drink: lines = new string[] { "喝好啦。", "咕噜，舒服了。" }; break;
                case InteractionKind.Pet: lines = new string[] { "嗯，这里舒服。", "再摸一下也可以。" }; break;
                case InteractionKind.Play: lines = new string[] { "抓到一点点啦。", "玩够啦，陪你歇会儿。" }; break;
                case InteractionKind.Scratch: lines = new string[] { "抓两下，舒服。", "爪爪舒展好了。" }; break;
                case InteractionKind.Climb: lines = new string[] { "巡视完啦，回来陪你。", "高处也看过啦。" }; break;
                default: return;
            }
            string line = lines[random.Next(lines.Length)];
#if REVIEW
            ReviewEvents.Write(kind == InteractionKind.Pet ? "pet.completed" : kind == InteractionKind.Play ? "play.completed" : "action.completed", kind, true, 1, 0);
#endif
            if (!String.IsNullOrEmpty(recycleOutcome)) { line = recycleOutcome + "\n" + line; recycleOutcome = null; }
            ShowBubble(line, 4); MarkInteraction();
#if REVIEW
            ReviewEvents.Write("feedback.completed", kind, true, 1, 0);
#endif
        }

        private void ShowBubble(string text, double seconds) { ShowBubble(text, seconds, BubbleAction.None); }
        private void ShowBubble(string text, double seconds, BubbleAction action)
        {
            bubbleText.Text = text;
            bubble.Visibility = Visibility.Visible;
            bubbleUntil = DateTime.UtcNow.AddSeconds(seconds);
            bubbleAction = action;
            bubble.IsHitTestVisible = action != BubbleAction.None;
            bubble.Cursor = action != BubbleAction.None ? Cursors.Hand : Cursors.Arrow;
            bubble.ToolTip = text;
        }

        private void HideBubble() { bubble.Visibility = Visibility.Collapsed; bubble.IsHitTestVisible = false; bubbleAction = BubbleAction.None; }

        private void OnBubbleClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true; TriggerBubbleAction(bubbleAction);
        }

        private void TriggerBubbleAction(BubbleAction action)
        {
            HideBubble(); MarkInteraction();
            if (action == BubbleAction.Play) BeginPlaySession();
            else if (action == BubbleAction.Pet) ShowPetHint();
            else if (action == BubbleAction.Food || action == BubbleAction.Water) ShowBubble("把要回收的普通文件拖到两个碗上。", 4);
        }

        private bool IsFood(Point point) { return new Rect(0, BubbleSpace + Width * .75, Width * .39, Width * .18).Contains(point); }
        private bool IsWithinCatCanvas(Point point) { return new Rect(0, BubbleSpace, Width, Width).Contains(point); }
        private bool IsVisibleCat(Point point)
        {
            if (!IsWithinCatCanvas(point)) return false;
            return cat.InputHitTest(new Point(point.X, point.Y - BubbleSpace)) != null;
        }
        private bool IsPetArea(Point point)
        {
            double x = point.X / Width, y = (point.Y - BubbleSpace) / Width;
            bool area = (x >= .25 && x <= .68 && y >= .10 && y <= .53) || (x >= .58 && x <= .93 && y >= .30 && y <= .65);
            return area && IsVisibleCat(point);
        }

        private void OnLeftDown(object sender, MouseButtonEventArgs e)
        {
            Point p = e.GetPosition(stage);
            if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0)
            {
                CancelInteraction();
#if REVIEW
                double previousLeft = Left, previousTop = Top;
#endif
                try
                {
                    DragMove(); Remember(); MarkInteraction();
#if REVIEW
                    ReviewEvents.Write("move.end", InteractionKind.Idle, Left != previousLeft || Top != previousTop, 0, 0);
#endif
                }
                catch (InvalidOperationException)
                {
#if REVIEW
                    ReviewEvents.Write("move.end", InteractionKind.Idle, false, 0, 0);
#endif
                }
                e.Handled = true; return;
            }
            if (playSession) { e.Handled = true; return; }
            if (processing || clickThrough || animation.IsBusy || animation.Paused) return;
            if (IsFood(p)) { ShowBubble("拖文件到粮碗，松开后移入回收站。", 5); e.Handled = true; return; }
            if (!IsPetArea(p)) return;
            petPending = true; petStarted = false; petPressedAt = clock.Elapsed.TotalMilliseconds;
            petLastMoveAt = petPressedAt; petLastPoint = p; petMovingMilliseconds = 0; petDistance = 0;
            if (!CaptureMouse()) { petPending = false; return; }
#if REVIEW
            ReviewEvents.Write("pet.pending", InteractionKind.Pet, true, 0, 0);
#endif
            e.Handled = true;
        }

        private void OnLeftUp(object sender, MouseButtonEventArgs e)
        {
            if (!petPending && !petStarted) return;
            FinishPettingGesture(IsPetCompletionValid(e.GetPosition(stage)));
            e.Handled = true;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            Point p = e.GetPosition(stage); double now = clock.Elapsed.TotalMilliseconds;
            Cursor = (Keyboard.Modifiers & ModifierKeys.Alt) != 0 ? Cursors.SizeAll : (!animation.IsBusy && !animation.Paused && IsPetArea(p) ? Cursors.Hand : Cursors.Arrow);
            if (petPending || petStarted)
            {
                if (e.LeftButton != MouseButtonState.Pressed || !IsPetArea(p)) { FinishPettingGesture(false); return; }
                TrackPetPoint(p, now);
            }
            if (playSession && !animation.Paused && IsWithinCatCanvas(p))
                TrackPlayPoint(p, now);
        }

        private void TrackPetPoint(Point point, double now)
        {
            double distance = (point - petLastPoint).Length;
            if (petStarted && distance > .25)
            {
                petDistance += distance;
                petMovingMilliseconds += Math.Min(150, Math.Max(0, now - petLastMoveAt));
            }
            petLastPoint = point; petLastMoveAt = now;
        }

        private bool IsPetCompletionValid(Point point)
        {
            return IsPetArea(point) && HasCompletedPetting();
        }

        private bool HasCompletedPetting() { return petStarted && petMovingMilliseconds >= 1000 && petDistance >= 24; }

        private void TrackPlayPoint(Point point, double now)
        {
            if (!playSession || animation.Paused || !IsWithinCatCanvas(point)) return;
            double distance = (point - playLastPoint).Length;
            if (distance > .4)
            {
                playDistance += distance;
                playMovingMilliseconds += Math.Min(120, Math.Max(0, now - playLastMoveAt));
            }
            playLastPoint = point; playLastMoveAt = now;
            Canvas.SetLeft(playDot, point.X - 4); Canvas.SetTop(playDot, point.Y - 4); playDot.Visibility = Visibility.Visible;
            animation.SetPlayTarget(point.X / Width);
            if (playMovingMilliseconds >= 6000 && playDistance >= 180) FinishPlaySession(true);
        }

        private void OnMouseLeave(object sender, MouseEventArgs e)
        {
            Cursor = Cursors.Arrow;
            if (petPending || petStarted) FinishPettingGesture(false);
            if (playSession) playDot.Visibility = Visibility.Collapsed;
        }

        private void OnLostMouseCapture(object sender, MouseEventArgs e)
        {
            if (petPending || petStarted) FinishPettingGesture(false);
            if (playSession) FinishPlaySession(false);
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { CancelInteraction(); e.Handled = true; }
        }

        private void FinishPettingGesture(bool completed)
        {
#if REVIEW
            bool pending = petPending;
#endif
            bool started = petStarted; petPending = false; petStarted = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            if (started) animation.EndPetting(completed);
#if REVIEW
            if (started || pending) ReviewEvents.Write("pet.end", InteractionKind.Pet, started && completed, 0, 0);
#endif
        }

        private bool BeginPlaySession()
        {
            if (processing || animation.IsBusy) { ShowBubble("包包先忙完这一小会儿。", 3); return false; }
            FinishPettingGesture(false); animation.Paused = false;
            if (!animation.BeginPlay()) return false;
            recycleOutcome = null; playSession = true;
            playStartedAt = playLastMoveAt = clock.Elapsed.TotalMilliseconds; actionStartedAt = playStartedAt;
            playLastPoint = new Point(Width * .6, BubbleSpace + Width * .5);
            playMovingMilliseconds = 0; playDistance = 0;
            playReceiver.Visibility = Visibility.Visible; playDot.Visibility = Visibility.Visible;
            Canvas.SetLeft(playDot, playLastPoint.X - 4); Canvas.SetTop(playDot, playLastPoint.Y - 4);
            if (IsVisible) { Activate(); Focus(); }
            MarkInteraction(); HideBubble();
#if REVIEW
            ReviewEvents.Write("play.begin", InteractionKind.Play, true, 0, 0);
#endif
            return true;
        }

        private void FinishPlaySession(bool completed)
        {
            bool started = playSession; playSession = false;
            playReceiver.Visibility = Visibility.Collapsed; playDot.Visibility = Visibility.Collapsed;
            if (started) animation.EndPlay(completed);
#if REVIEW
            if (started) ReviewEvents.Write("play.end", InteractionKind.Play, completed, 0, 0);
#endif
        }

        private void CancelInteraction()
        {
            FinishPettingGesture(false); FinishPlaySession(false);
            previewPetUntil = 0; recycleOutcome = null; animation.ReturnToIdle();
        }

        private void ShowPetHint() { ShowBubble("在头或上背长按，再轻轻来回移动。", 5); MarkInteraction(); }
        private void PreviewEat() { PreviewAction(InteractionKind.Eat); }
        private void PreviewAction(InteractionKind kind)
        {
            if (kind == InteractionKind.Play) { BeginPlaySession(); return; }
            if (processing || animation.IsBusy) { ShowBubble("包包先忙完这一小会儿。", 3); return; }
            animation.Paused = false;
            if (animation.TryStart(kind)) { recycleOutcome = null; actionStartedAt = clock.Elapsed.TotalMilliseconds; MarkInteraction(); HideBubble(); }
        }

        internal void StartDeveloperPreview(string actionId)
        {
            if (String.IsNullOrEmpty(actionId) || processing) return;
            CancelInteraction(); animation.Reset();
            if (actionId == "play")
            {
                BeginPlaySession();
#if REVIEW
                if (playSession) ReviewEvents.Write("preview.begin", InteractionKind.Play, true, 0, 0);
#endif
                return;
            }
            if (animation.TryStartAction(actionId))
            {
                actionStartedAt = clock.Elapsed.TotalMilliseconds; MarkInteraction();
                if (actionId == "pet") previewPetUntil = actionStartedAt + 2000;
                ShowBubble("动作演示", 1.5);
#if REVIEW
                ReviewEvents.Write("preview.begin", animation.CurrentKind, true, 0, 0);
#endif
            }
        }
        private string[] DragPaths(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
            return e.Data.GetData(DataFormats.FileDrop) as string[];
        }
        private void OnDragOver(object sender, DragEventArgs e)
        {
            e.Handled = true; e.Effects = DragDropEffects.None;
            string[] paths = DragPaths(e);
            DropPlan plan = dropRouter.Plan(paths);
            Point point = e.GetPosition(stage);
            string reason;
            if ((e.AllowedEffects & DragDropEffects.Copy) == 0) reason = "请从文件资源管理器拖入文件。";
            else reason = DropRejection(plan, IsFood(point), IsVisibleCat(point));
            if (reason == null)
            {
                e.Effects = DragDropEffects.Copy;
                reason = plan.Kind == DropKind.Toy ? "松开来玩" + plan.DisplayName + "，玩具文件会保留。" : "松开后，移入系统回收站；包包会吃粮或喝水。";
            }
            foodGlow.Visibility = e.Effects == DragDropEffects.Copy ? Visibility.Visible : Visibility.Collapsed;
            if (dragReason != reason || bubble.Visibility != Visibility.Visible) { dragReason = reason; ShowBubble(reason, 4); }
        }
        private void HideDragHint() { foodGlow.Visibility = Visibility.Collapsed; dragReason = null; }
        private void OnDrop(object sender, DragEventArgs e)
        {
            e.Handled = true; e.Effects = DragDropEffects.None;
            HideDragHint();
            if ((e.AllowedEffects & DragDropEffects.Copy) == 0) return;
            string[] paths = DragPaths(e);
            // Copy only acknowledges receipt of path data. Returning Move before the
            // asynchronous recycle completes could tell an OLE source to delete it.
            Point point = e.GetPosition(stage);
            if (ReceiveDroppedFiles(paths, IsFood(point), IsVisibleCat(point))) e.Effects = DragDropEffects.Copy;
        }

        private string DropRejection(DropPlan plan, bool overBowls, bool overCat)
        {
            if (plan.Kind == DropKind.Rejected) return plan.Message;
            if (processing || animation.IsBusy || petPending) return "包包正在忙，请等这一小会儿。";
            if (clickThrough) return "请先从托盘关闭鼠标穿透。";
            if (plan.Kind == DropKind.Toy) return overCat ? null : "把玩具文件拖到包包身上，文件会保留。";
            if (!overBowls) return "普通文件请拖到两个碗上，松开后移入回收站。";
            foreach (string path in plan.Paths)
            {
                string reason = RecycleService.ValidatePath(path);
                if (reason != null) return reason;
            }
            return null;
        }

        internal bool ReceiveDroppedFiles(string[] paths, bool overBowls, bool overCat)
        {
            DropPlan plan = dropRouter.Plan(paths);
            string reason = DropRejection(plan, overBowls, overCat);
            if (reason != null)
            {
                ShowBubble(reason, 5);
#if REVIEW
                ReviewEvents.Write("drop.rejected", animation.CurrentKind, false, 0, plan.Paths.Length);
#endif
                return false;
            }
            if (plan.Kind == DropKind.Toy)
            {
                animation.Paused = false;
                if (!animation.TryStartAction(plan.ActionId)) { ShowBubble("暂时不能使用这件玩具，文件仍在原处。", 5); return false; }
                recycleOutcome = null; actionStartedAt = clock.Elapsed.TotalMilliseconds; MarkInteraction();
                ShowBubble("玩具文件已保留，陪包包玩一会儿。", 3);
#if REVIEW
                ReviewEvents.Write("drop.toy.accepted", animation.CurrentKind, true, 1, 0);
#endif
                return true;
            }
            RecycleDroppedFiles(plan.Paths); return processing;
        }

        internal void RecycleDroppedFiles(string[] paths)
        {
            DropPlan plan = dropRouter.Plan(paths);
            string reason = DropRejection(plan, true, true);
            if (reason != null) { ShowBubble(reason, 5); return; }
            // The internal recycle entry point also routes first: special toy
            // files and rejected manifests can never fall through to deletion.
            if (plan.Kind != DropKind.OrdinaryFiles) { ShowBubble("玩具文件请投到包包身上，文件会保留。", 4); return; }
            string[] files = (string[])plan.Paths.Clone();
            processing = true;
            MarkInteraction();
            ShowBubble("正在移入系统回收站……", 30);
#if REVIEW
            ReviewEvents.Write("recycle.begin", InteractionKind.Idle, true, files.Length, 0);
#endif
            Thread worker = new Thread(delegate()
            {
                RecycleBatchResult result = null;
                string failure = null;
                try { result = RecycleService.Recycle(files, handle); }
                catch (Exception ex) { failure = "回收未完成：" + ex.Message; }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    processing = false;
                    if (failure != null)
                    {
                        ShowBubble(failure, 7);
#if REVIEW
                        ReviewEvents.Write("recycle.result", InteractionKind.Idle, false, 0, files.Length);
#endif
                        return;
                    }
                    if (result.SuccessCount > 0)
                    {
                        animation.Paused = false;
                        string summary = "已回收 " + result.SuccessCount + " 个文件。";
                        if (result.FailureCount > 0) summary += "另有 " + result.FailureCount + " 个未完成。";
                        recycleOutcome = summary;
                        if (animation.Feed()) { actionStartedAt = clock.Elapsed.TotalMilliseconds; MarkInteraction(); }
                        ShowBubble(summary, 6);
                    }
                    else ShowBubble(result.Items.Count > 0 ? result.Items[0].Message : "没有可回收的文件。", 7);
#if REVIEW
                    ReviewEvents.Write("recycle.result", animation.CurrentKind, result.SuccessCount > 0, result.SuccessCount, result.FailureCount);
#endif
                    if (result.FailureCount > 0 && result.SuccessCount > 0)
                    {
                        tray.BalloonTipTitle = "包包：部分文件未回收";
                        string detail = null;
                        foreach (RecycleItemResult item in result.Items) if (!item.Success) { detail = System.IO.Path.GetFileName(item.Path) + "：" + item.Message; break; }
                        tray.BalloonTipText = detail ?? "请检查未完成的文件。";
                        tray.ShowBalloonTip(6000);
                    }
                }));
            });
            worker.IsBackground = true; worker.SetApartmentState(ApartmentState.STA); worker.Start();
        }

        private MenuItem Menu(string label, Action action)
        {
            MenuItem item = new MenuItem(); item.Header = label; item.Click += delegate { action(); }; return item;
        }
        private MenuItem CheckMenu(string label, bool value, Action action)
        {
            MenuItem item = Menu(label, action); item.IsCheckable = true; item.IsChecked = value; return item;
        }
        private MenuItem ActionMenu(string label, InteractionKind kind)
        {
            MenuItem item = Menu(label, delegate { PreviewAction(kind); }); item.IsEnabled = !processing && !animation.IsBusy; return item;
        }
        private void TogglePause()
        {
            bool paused = !animation.Paused;
            if (paused && (petPending || petStarted || playSession)) CancelInteraction();
            animation.Paused = paused; MarkInteraction();
        }
        private void ToggleQuiet() { settings.Quiet = !settings.Quiet; settings.Save(); if (settings.Quiet) HideBubble(); }
        private void HidePet() { CancelInteraction(); MarkInteraction(); Hide(); }
        private void OpenMenu()
        {
            ContextMenu menu = new ContextMenu();
            MenuItem title = new MenuItem(); title.Header = "包包 · 10岁 · V1.0"; title.IsEnabled = false; menu.Items.Add(title);
            menu.Items.Add(ActionMenu("陪玩(_P)", InteractionKind.Play));
            menu.Items.Add(Menu("摸摸提示(_M)", ShowPetHint));
#if REVIEW
            menu.Items.Add(Menu("抚摸验收(_V)", delegate
            {
                StartDeveloperPreview("pet");
                if (!processing && animation.CurrentKind == InteractionKind.Pet && previewPetUntil > 0)
                    previewPetUntil = clock.Elapsed.TotalMilliseconds + 9000;
            }));
#endif
            menu.Items.Add(ActionMenu("吃粮预览(_F)", InteractionKind.Eat));
            menu.Items.Add(ActionMenu("喝水预览(_D)", InteractionKind.Drink));
            menu.Items.Add(ActionMenu("猫抓板(_S)", InteractionKind.Scratch));
            menu.Items.Add(ActionMenu("猫爬架(_C)", InteractionKind.Climb));
            MenuItem end = Menu("结束互动(_E)", CancelInteraction); end.IsEnabled = animation.IsBusy || petPending; menu.Items.Add(end);
            menu.Items.Add(Menu("打开系统回收站(_R)", OpenRecycleBin));
            menu.Items.Add(new Separator());
            menu.Items.Add(CheckMenu("暂停动作(_A)", animation.Paused, TogglePause));
            menu.Items.Add(CheckMenu("少说话(_Q)", settings.Quiet, ToggleQuiet));
            menu.Items.Add(CheckMenu("始终置顶(_T)", Topmost, delegate { Topmost = !Topmost; Remember(); }));
            menu.Items.Add(CheckMenu("鼠标穿透，从托盘恢复(_H)", clickThrough, delegate { SetClickThrough(!clickThrough); }));
            MenuItem sizes = new MenuItem(); sizes.Header = "大小(_Z)";
            sizes.Items.Add(Menu("小 · 240(_1)", delegate { ResizePet(240); }));
            sizes.Items.Add(Menu("中 · 300(_2)", delegate { ResizePet(300); }));
            sizes.Items.Add(Menu("大 · 380(_3)", delegate { ResizePet(380); })); menu.Items.Add(sizes);
            menu.Items.Add(Menu("恢复位置与大小(_O)", delegate { ResizePet(300); PositionPet(true); }));
            menu.Items.Add(Menu("隐藏到托盘(_N)", HidePet));
            menu.Items.Add(new Separator());
            MenuItem quit = Menu("退出包包(_X)", ExitPet); quit.IsEnabled = !processing; menu.Items.Add(quit);
            menu.PlacementTarget = cat; menu.IsOpen = true;
        }
        private void OpenRecycleBin() { Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true }); }
        private Forms.ToolStripMenuItem TrayItem(string label, bool check, bool value, Action action)
        {
            Forms.ToolStripMenuItem item = new Forms.ToolStripMenuItem(label.Replace("_", "&")); item.Checked = check && value; item.Click += delegate { Dispatcher.BeginInvoke(action); }; return item;
        }
        private Forms.ToolStripMenuItem TrayAction(string label, InteractionKind kind)
        {
            Forms.ToolStripMenuItem item = TrayItem(label, false, false, delegate { ShowPet(); PreviewAction(kind); });
            item.Enabled = !processing && !animation.IsBusy; return item;
        }
        private void BuildTrayMenu()
        {
            while (trayMenu.Items.Count > 0) { Forms.ToolStripItem old = trayMenu.Items[0]; trayMenu.Items.RemoveAt(0); old.Dispose(); }
            trayMenu.Items.Add(TrayItem("显示包包 / 关闭穿透(_B)", false, false, ShowPet));
            trayMenu.Items.Add(TrayAction("陪玩(_P)", InteractionKind.Play));
            trayMenu.Items.Add(TrayItem("摸摸提示(_M)", false, false, delegate { ShowPet(); ShowPetHint(); }));
            trayMenu.Items.Add(TrayAction("吃粮预览(_F)", InteractionKind.Eat));
            trayMenu.Items.Add(TrayAction("喝水预览(_D)", InteractionKind.Drink));
            trayMenu.Items.Add(TrayAction("猫抓板(_S)", InteractionKind.Scratch));
            trayMenu.Items.Add(TrayAction("猫爬架(_C)", InteractionKind.Climb));
            Forms.ToolStripMenuItem end = TrayItem("结束互动(_E)", false, false, CancelInteraction); end.Enabled = animation.IsBusy || petPending; trayMenu.Items.Add(end);
            trayMenu.Items.Add(TrayItem("打开系统回收站(_R)", false, false, OpenRecycleBin));
            trayMenu.Items.Add(new Forms.ToolStripSeparator());
            trayMenu.Items.Add(TrayItem("暂停动作(_A)", true, animation.Paused, TogglePause));
            trayMenu.Items.Add(TrayItem("少说话(_Q)", true, settings.Quiet, ToggleQuiet));
            trayMenu.Items.Add(TrayItem("始终置顶(_T)", true, Topmost, delegate { Topmost = !Topmost; Remember(); }));
            trayMenu.Items.Add(TrayItem("鼠标穿透(_H)", true, clickThrough, delegate { SetClickThrough(!clickThrough); }));
            trayMenu.Items.Add(TrayItem("恢复位置与大小(_O)", false, false, delegate { ShowPet(); ResizePet(300); PositionPet(true); }));
            trayMenu.Items.Add(TrayItem("隐藏包包(_N)", false, false, HidePet));
            trayMenu.Items.Add(new Forms.ToolStripSeparator());
            Forms.ToolStripMenuItem quit = TrayItem("退出包包(_X)", false, false, ExitPet); quit.Enabled = !processing; trayMenu.Items.Add(quit);
        }
        private void CreateTray()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream stream = assembly.GetManifestResourceStream("trayicon"))
            {
                if (stream == null) throw new InvalidDataException("缺少包包的托盘图标。");
                using (Drawing.Icon loaded = new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize))
                    trayIcon = (Drawing.Icon)loaded.Clone();
            }
            using (Stream stream = assembly.GetManifestResourceStream("trayicon"))
            {
                BitmapFrame windowIcon = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                windowIcon.Freeze(); Icon = windowIcon;
            }
            tray.Icon = trayIcon;
            tray.Text = "包包 V1.0 · 右键打开菜单";
            tray.Visible = true;
            tray.ContextMenuStrip = trayMenu;
            trayMenu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e) { BuildTrayMenu(); e.Cancel = false; };
            tray.DoubleClick += delegate { Dispatcher.BeginInvoke(new Action(ShowPet)); };
        }
        private void ExitPet()
        {
            if (processing) { ShowBubble("正在回收文件，请稍等一下。", 4); return; }
            CancelInteraction(); exiting = true; Close();
        }
        private void OnClosed(object sender, EventArgs e)
        {
            exiting = true;
            CancelInteraction();
            Remember(); timer.Stop();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            if (source != null) source.RemoveHook(WindowHook);
            tray.Visible = false; tray.Dispose(); trayMenu.Dispose(); if (trayIcon != null) trayIcon.Dispose();
        }
    }

#if REVIEW
    /// <summary>Local acceptance evidence only; absent from a production build.</summary>
    internal static class ReviewEvents
    {
        private static readonly object Gate = new object();

        internal static void Write(string eventName, InteractionKind action, bool success, int count, int failures)
        {
            try
            {
                string line = String.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:o} event={1} action={2} success={3} count={4} failures={5}\r\n",
                    DateTime.UtcNow, eventName, action, success ? "true" : "false", count, failures);
                string file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "verification-events.log");
                lock (Gate) File.AppendAllText(file, line, new System.Text.UTF8Encoding(false));
            }
            catch (Exception) { /* Acceptance logging must never change interaction behavior. */ }
        }
    }
#endif

    public static class Program
    {
        private const int WM_SHOW_BAOBAO = 0x8000 + 42;
        private const int WM_PREVIEW_BAOBAO = 0x8000 + 43;
        private const int WM_CLOSE = 0x0010;
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        internal static string PreviewAction(int code)
        {
            switch (code)
            {
                case 1: return "eat";
                case 2: return "drink";
                case 3: return "play";
                case 4: return "pet";
                case 5: return "toy.scratcher";
                case 6: return "toy.cat_tree";
                default: return null;
            }
        }

        private static int PreviewCode(string[] args)
        {
            if (args == null) return 0;
            foreach (string argument in args)
                if (argument != null && argument.StartsWith("--preview=", StringComparison.Ordinal))
                    for (int code = 1; code <= 6; code++)
                        if (argument.Substring(10) == PreviewAction(code)) return code;
            return 0;
        }

        private static bool TakeMutex(Mutex mutex, int milliseconds)
        {
            try { return mutex.WaitOne(milliseconds); }
            catch (AbandonedMutexException) { return true; }
        }

        internal static bool IsKnownBaobaoExecutable(string path, out Version version)
        {
            version = null;
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                if (info.FileDescription != "包包桌面摆件" || !String.Equals(info.OriginalFilename, "Baobao.exe", StringComparison.OrdinalIgnoreCase)) return false;
                Version fileVersion;
                if (!Version.TryParse(info.FileVersion, out fileVersion) || fileVersion < new Version(0, 1, 0, 0)) return false;
                Version assemblyVersion = AssemblyName.GetAssemblyName(path).Version;
                if (assemblyVersion != fileVersion) return false;
                version = fileVersion; return true;
            }
            catch (Exception) { return false; }
        }

        private static bool ExistingIdentity(IntPtr window, out string path, out Version version)
        {
            path = null; version = null;
            try
            {
                uint processId;
                if (GetWindowThreadProcessId(window, out processId) == 0 || processId == 0 || processId == Process.GetCurrentProcess().Id) return false;
                using (Process process = Process.GetProcessById((int)processId))
                {
                    if (process.HasExited) return false;
                    path = Path.GetFullPath(process.MainModule.FileName);
                    return IsKnownBaobaoExecutable(path, out version);
                }
            }
            catch (Exception) { return false; }
        }

        [STAThread]
        public static void Main(string[] args)
        {
            int previewCode = PreviewCode(args);
            using (Mutex mutex = new Mutex(false, "Local\\BaobaoDesktop_v1"))
            {
                bool owned = TakeMutex(mutex, 0);
                if (!owned)
                {
                    IntPtr existing = FindWindow(null, "包包桌面摆件");
                    for (int attempt = 0; existing == IntPtr.Zero && !owned && attempt < 12; attempt++)
                    {
                        Thread.Sleep(50); owned = TakeMutex(mutex, 0);
                        if (!owned) existing = FindWindow(null, "包包桌面摆件");
                    }
                    if (!owned)
                    {
                        string existingPath; Version existingVersion;
                        if (existing == IntPtr.Zero || !ExistingIdentity(existing, out existingPath, out existingVersion))
                        {
                            MessageBox.Show("已有包包实例，但无法安全确认其程序版本。请退出旧实例后再启动新版。", "包包桌面摆件", MessageBoxButton.OK, MessageBoxImage.Information);
                            return;
                        }
                        string currentPath = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
                        Version currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
                        bool samePath = String.Equals(currentPath, existingPath, StringComparison.OrdinalIgnoreCase);
                        if (samePath || existingVersion >= currentVersion)
                        {
                            PostMessage(existing, WM_SHOW_BAOBAO, IntPtr.Zero, IntPtr.Zero);
                            if (previewCode != 0 && existingVersion >= new Version(1, 0, 0, 0)) PostMessage(existing, WM_PREVIEW_BAOBAO, new IntPtr(previewCode), IntPtr.Zero);
                            return;
                        }
                        // A verified older Baobao in a different directory may
                        // close normally. Its own busy Closing guard can refuse.
                        PostMessage(existing, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                        owned = TakeMutex(mutex, 3000);
                        if (!owned)
                        {
                            MessageBox.Show("旧版包包尚未退出，请等当前文件处理完成后再启动新版。", "包包桌面摆件", MessageBoxButton.OK, MessageBoxImage.Information);
                            return;
                        }
                    }
                }
                try
                {
                    Application app = new Application();
                    app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                    PetWindow pet = new PetWindow();
                    if (previewCode != 0) pet.Loaded += delegate { pet.StartDeveloperPreview(PreviewAction(previewCode)); };
                    app.Run(pet);
                }
                catch (Exception ex)
                {
                    try { Directory.CreateDirectory(PetSettings.Folder); File.WriteAllText(System.IO.Path.Combine(PetSettings.Folder, "last-error.txt"), ex.ToString()); } catch { }
                    MessageBox.Show("包包暂时没能启动：" + ex.Message, "包包桌面摆件", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally { if (owned) mutex.ReleaseMutex(); }
            }
        }
    }
}
