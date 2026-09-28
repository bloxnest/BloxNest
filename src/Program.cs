// BloxNest: opens more than one Roblox window at once. Made by xRed1.
// The window layout lives in MainWindow.xaml. Build with build.bat (uses the C#
// compiler and WPF that ship with Windows, nothing to install).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using IOPath = System.IO.Path;

[assembly: AssemblyTitle("BloxNest")]
[assembly: AssemblyDescription("BloxNest - open more than one Roblox window at once")]
[assembly: AssemblyCompany("xRed1")]
[assembly: AssemblyProduct("BloxNest")]
[assembly: AssemblyCopyright("Copyright (c) 2026 xRed1")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]

namespace BloxNest
{
    static class Program
    {
        static Mutex singleInstance;

        [STAThread]
        static int Main()
        {
            // Only one BloxNest runs at a time. Opening it again brings the running one back.
            bool first;
            singleInstance = new Mutex(true, "BloxNest.SingleInstance", out first);
            if (!first)
            {
                try { EventWaitHandle.OpenExisting("BloxNest.Show").Set(); } catch { }
                return 0;
            }
            var showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, "BloxNest.Show");

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var app = new Application();
            int code = app.Run(new MainWindow(showRequest).Window);
            GC.KeepAlive(singleInstance);
            return code;
        }
    }

    // Options, saved in %AppData%\BloxNest\settings.ini
    class Settings
    {
        static readonly string FilePath = IOPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BloxNest", "settings.ini");

        public bool Tray = true;          // closing keeps BloxNest in the hidden icons
        public bool Update = true;        // update Roblox before opening windows
        public bool Notify = true;        // tell me when a new Roblox is out
        public int Delay = 5;             // seconds between windows
        public int Count = 2;             // last 1-5 choice
        public int Custom;                // last number typed in Custom (0 = none)
        public bool TrayTipShown;
        public string NotifiedVersion = "";

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                    int n;
                    int.TryParse(value, out n);
                    switch (key)
                    {
                        case "tray": s.Tray = value == "1"; break;
                        case "update": s.Update = value == "1"; break;
                        case "notify": s.Notify = value == "1"; break;
                        case "delay": if (n == 3 || n == 5 || n == 8) s.Delay = n; break;
                        case "count": if (n >= 1 && n <= 5) s.Count = n; break;
                        case "custom": s.Custom = n; break;
                        case "trayTipShown": s.TrayTipShown = value == "1"; break;
                        case "notifiedVersion": s.NotifiedVersion = value; break;
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(IOPath.GetDirectoryName(FilePath));
                File.WriteAllLines(FilePath, new[]
                {
                    "tray=" + (Tray ? 1 : 0),
                    "update=" + (Update ? 1 : 0),
                    "notify=" + (Notify ? 1 : 0),
                    "delay=" + Delay,
                    "count=" + Count,
                    "custom=" + Custom,
                    "trayTipShown=" + (TrayTipShown ? 1 : 0),
                    "notifiedVersion=" + NotifiedVersion,
                });
            }
            catch { }
        }
    }

    class RobloxVersion
    {
        public string Version;   // e.g. 0.740.0.7400927
        public string Folder;    // e.g. version-2366ba214ec740ca

        public string ShortVersion
        {
            get { string[] p = Version.Split('.'); return p.Length >= 2 ? p[0] + "." + p[1] : Version; }
        }
    }

    static class Roblox
    {
        public const int ExitStillOpen = 3;

        static readonly string VersionsDir = IOPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Roblox\Versions");

        public static int CountOpen()
        {
            Process[] found = Process.GetProcessesByName("RobloxPlayerBeta");
            foreach (Process p in found) p.Dispose();
            return found.Length;
        }

        // Asks every Roblox window to close, and only forces the ones that don't
        public static void CloseAll()
        {
            Process[] running = Process.GetProcessesByName("RobloxPlayerBeta");
            foreach (Process p in running)
            {
                try { p.CloseMainWindow(); } catch { }
            }
            foreach (Process p in running)
            {
                try
                {
                    if (!p.WaitForExit(4000))
                    {
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                }
                catch { }
                p.Dispose();
            }
        }

        // The Roblox version currently installed and registered with Windows
        public static string FindPlayer()
        {
            try
            {
                using (RegistryKey key = Registry.ClassesRoot.OpenSubKey(@"roblox-player\shell\open\command"))
                {
                    string cmd = key == null ? null : key.GetValue("") as string;
                    if (cmd != null)
                    {
                        string[] parts = cmd.Split('"');
                        if (parts.Length > 1 && File.Exists(parts[1])) return parts[1];
                    }
                }
            }
            catch { }
            return NewestIn("RobloxPlayerBeta.exe");
        }

        // e.g. "0.740", read from the Roblox file itself
        public static string ShortVersionOf(string exe)
        {
            if (exe == null) return null;
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(exe);
                return info.FileMajorPart + "." + info.FileMinorPart;
            }
            catch { return null; }
        }

        // Roblox keeps its own updater next to the game
        public static string FindUpdater()
        {
            return NewestIn("RobloxPlayerInstaller.exe");
        }

        static string NewestIn(string fileName)
        {
            if (!Directory.Exists(VersionsDir)) return null;
            return Directory.GetDirectories(VersionsDir)
                .Select(d => IOPath.Combine(d, fileName))
                .Where(File.Exists)
                .OrderByDescending(f => File.GetLastWriteTime(f))
                .FirstOrDefault();
        }

        // Ask Roblox which version is newest. Returns null if that can't be reached.
        public static RobloxVersion GetLatest()
        {
            try
            {
                using (var web = new WebClient())
                {
                    string json = web.DownloadString("https://clientsettingscdn.roblox.com/v2/client-version/WindowsPlayer");
                    Match v = Regex.Match(json, "\"version\"\\s*:\\s*\"([^\"]+)\"");
                    Match f = Regex.Match(json, "\"clientVersionUpload\"\\s*:\\s*\"(version-[0-9a-f]+)\"");
                    if (!v.Success || !f.Success) return null;
                    return new RobloxVersion { Version = v.Groups[1].Value, Folder = f.Groups[1].Value };
                }
            }
            catch { return null; }
        }

        public static string PlayerIn(RobloxVersion version)
        {
            string exe = IOPath.Combine(VersionsDir, version.Folder, "RobloxPlayerBeta.exe");
            return File.Exists(exe) ? exe : null;
        }

        // Runs Roblox's updater and waits for it. It opens Roblox when it's done, so that
        // window gets closed again (BloxNest needs Roblox closed to open its own).
        public static void RunUpdater(string updater)
        {
            using (Process p = Process.Start(updater))
                p.WaitForExit(5 * 60 * 1000);
            Thread.Sleep(3000);
            CloseAll();
        }

        public static int LaunchWindows(int count, string exe, int delaySeconds)
        {
            // Roblox allows one window by creating a lock called ROBLOX_singletonEvent.
            // Normally we grab that name first as a different kind of object, so Roblox
            // can't create its lock and skips the check. If something on the PC is still
            // holding an old Roblox lock open, we lock that one so Roblox can't use it.
            WaitHandle eventLock = null;
            for (int attempt = 0; eventLock == null; attempt++)
            {
                eventLock = ClaimSingletonEvent();
                if (eventLock == null)
                {
                    if (attempt >= 20) return ExitStillOpen;
                    Thread.Sleep(500);
                }
            }
            Mutex oldLock = null;   // older Roblox versions used this one
            try { oldLock = new Mutex(true, "ROBLOX_singletonMutex"); } catch { }

            string dir = IOPath.GetDirectoryName(exe);
            string name = IOPath.GetFileNameWithoutExtension(exe);
            string ext = IOPath.GetExtension(exe);
            for (int i = 0; i < count; i++)
            {
                // If the old lock went away since, claim the name before Roblox can
                if (eventLock == AlreadyLocked)
                    eventLock = TryClaimName() ?? AlreadyLocked;

                string path = IOPath.Combine(dir, Spelling(name, i) + ext);
                using (Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })) { }
                Thread.Sleep(delaySeconds * 1000);
            }

            // The locks only matter while the windows start up
            Thread.Sleep(10000);
            if (eventLock != AlreadyLocked) eventLock.Dispose();
            if (oldLock != null) oldLock.Dispose();
            return 0;
        }

        // Roblox also finds an already-open window by the exact text of its file path.
        // Windows ignores upper/lower case in paths but Roblox doesn't, so window i gets
        // the file name with the case of some letters flipped, picked by the bits of i
        // (0 = RobloxPlayerBeta, 1 = robloxPlayerBeta, 2 = RObloxPlayerBeta, ...), which
        // is a different spelling for every window. Only the file name is changed because
        // Windows tidies up the case of the C:\Users\<name> part on its own.
        static string Spelling(string name, int i)
        {
            char[] chars = name.ToCharArray();
            for (int bit = 0; bit < chars.Length && (i >> bit) != 0; bit++)
            {
                if (((i >> bit) & 1) == 0) continue;
                chars[bit] = char.IsUpper(chars[bit]) ? char.ToLowerInvariant(chars[bit]) : char.ToUpperInvariant(chars[bit]);
            }
            return new string(chars);
        }

        const string SingletonEvent = "ROBLOX_singletonEvent";

        // Stands in for a lock we don't need to hold: the old one is already unusable to Roblox
        static readonly WaitHandle AlreadyLocked = new ManualResetEvent(false);

        // Returns a lock we hold, AlreadyLocked, or null if Roblox could still use its lock
        static WaitHandle ClaimSingletonEvent()
        {
            WaitHandle mine = TryClaimName();
            if (mine != null) return mine;

            // An old Roblox lock is still around: take away everyone's access to it
            try
            {
                EventWaitHandle old = EventWaitHandle.OpenExisting(SingletonEvent,
                    EventWaitHandleRights.ChangePermissions | EventWaitHandleRights.ReadPermissions);
                var noAccess = new EventWaitHandleSecurity();
                noAccess.SetAccessRuleProtection(true, false);   // empty permission list: nobody can use it
                old.SetAccessControl(noAccess);
                return old;   // holding it keeps it from disappearing mid-launch
            }
            catch (UnauthorizedAccessException)
            {
                // We can't change it (an admin program owns it). If Roblox can't open it
                // either, Roblox skips its one-window check, which is all we need.
                try
                {
                    using (new EventWaitHandle(false, EventResetMode.ManualReset, SingletonEvent)) { }
                    return null;
                }
                catch (UnauthorizedAccessException) { return AlreadyLocked; }
                catch { return null; }
            }
            catch { return null; }   // it vanished in between, so the next try can claim the name
        }

        // Claims the name as a different kind of object, so Roblox can't create its lock
        static WaitHandle TryClaimName()
        {
            try { return new Mutex(true, SingletonEvent); }
            catch { return null; }
        }
    }

    // Checks that Roblox's updater really comes from Roblox before running it
    static class Signature
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        class WinTrustFileInfo
        {
            public uint cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo));
            public string pcwszFilePath;
            public IntPtr hFile = IntPtr.Zero;
            public IntPtr pgKnownSubject = IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        class WinTrustData
        {
            public uint cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustData));
            public IntPtr pPolicyCallbackData = IntPtr.Zero;
            public IntPtr pSIPClientData = IntPtr.Zero;
            public uint dwUIChoice = 2;          // no UI
            public uint fdwRevocationChecks = 0;
            public uint dwUnionChoice = 1;       // a file
            public IntPtr pFile;
            public uint dwStateAction = 0;
            public IntPtr hWVTStateData = IntPtr.Zero;
            public IntPtr pwszURLReference = IntPtr.Zero;
            public uint dwProvFlags = 0;
            public uint dwUIContext = 0;
            public IntPtr pSignatureSettings = IntPtr.Zero;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, WinTrustData data);

        static readonly Guid VerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        public static bool IsSignedByRoblox(string path)
        {
            var file = new WinTrustFileInfo { pcwszFilePath = path };
            IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf(file));
            try
            {
                Marshal.StructureToPtr(file, pFile, false);
                if (WinVerifyTrust(new IntPtr(-1), VerifyV2, new WinTrustData { pFile = pFile }) != 0) return false;
                string subject = X509Certificate.CreateFromSignedFile(path).Subject;
                return subject.Contains("O=Roblox Corporation");
            }
            catch { return false; }
            finally { Marshal.FreeHGlobal(pFile); }
        }
    }

    // Colours for the dark right-click menu of the hidden-icons icon
    class TrayMenuColors : Forms.ProfessionalColorTable
    {
        static readonly Drawing.Color Back = Drawing.Color.FromArgb(0x21, 0x26, 0x1E);
        static readonly Drawing.Color Hover = Drawing.Color.FromArgb(0x30, 0x37, 0x2C);
        static readonly Drawing.Color Line = Drawing.Color.FromArgb(0x33, 0x3B, 0x2D);

        public override Drawing.Color ToolStripDropDownBackground { get { return Back; } }
        public override Drawing.Color ImageMarginGradientBegin { get { return Back; } }
        public override Drawing.Color ImageMarginGradientMiddle { get { return Back; } }
        public override Drawing.Color ImageMarginGradientEnd { get { return Back; } }
        public override Drawing.Color MenuBorder { get { return Line; } }
        public override Drawing.Color MenuItemBorder { get { return Hover; } }
        public override Drawing.Color MenuItemSelected { get { return Hover; } }
        public override Drawing.Color SeparatorDark { get { return Line; } }
        public override Drawing.Color SeparatorLight { get { return Back; } }
    }

    class MainWindow
    {
        const string PlayIcon = "\uE768", RestartIcon = "\uE72C", WarnIcon = "\uE7BA", WaitIcon = "\uE916";
        const int MaxWindows = 20;

        static readonly Brush Gray = Hex("#6B717C"), Blue = Hex("#6D8BFF"), Green = Hex("#46CD82"),
                              Amber = Hex("#F5B94B"), Red = Hex("#F56464");
        static readonly Brush SelectedSurface = Hex("#E6EBDE"), SelectedText = Hex("#171A16"),
                              LightText = Hex("#EEF1E9"), Lime = Hex("#D5F478"), Muted = Hex("#7E8876");
        static readonly Color StartColor = (Color)ColorConverter.ConvertFromString("#D5F478");
        static readonly Color DangerColor = (Color)ColorConverter.ConvertFromString("#E5484D");

        public readonly Window Window;
        readonly Settings settings = Settings.Load();
        readonly Button startButton;
        readonly TextBlock startIcon, startText, statusText, customHint, robloxVersionText, versionDetailText;
        readonly TextBox customCount;
        readonly Border customSurface;
        readonly System.Windows.Shapes.Ellipse dot;
        readonly FrameworkElement windowRoot, progressWrap, mainPanel, aboutPanel, settingsPanel;
        readonly ScaleTransform rootScale, progressScale;
        readonly RadioButton[] segments, delaySegments;
        readonly CheckBox optTray, optUpdate, optNotify;
        readonly SolidColorBrush startFill;
        readonly Brush startText0;
        readonly DispatcherTimer poll, confirmTimeout, versionTimer;
        Forms.NotifyIcon tray;

        int radioCount;         // the 1-5 choice
        int? custom;            // number typed in the Custom box, if any
        int open = -1;          // Roblox windows open right now (-1 = not checked yet)
        bool checking;
        string busy;            // null, "closing", "checking", "updating" or "opening"
        string updateText;
        int requested;          // windows asked for by the last Start
        string runningVersion;  // Roblox version the last Start opened, e.g. "0.740 (latest)"
        bool launchedByUs;      // the open windows came from our last Start
        string error;           // what went wrong with the last Start, if anything
        Brush errorColor;
        bool confirming;        // showing "click again to close Roblox"
        bool pulsing, dangerShown;
        double shownProgress = -1;
        RobloxVersion latestKnown;
        DateTime lastChecked;
        bool versionChecking;
        bool quitting, hiding;
        FrameworkElement currentPanel;

        int Count { get { return custom ?? radioCount; } }
        bool CountValid { get { return Count >= 1 && Count <= MaxWindows; } }

        public MainWindow(EventWaitHandle showRequest)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml"))
                Window = (Window)XamlReader.Load(s);

            windowRoot = Find<FrameworkElement>("WindowRoot");
            rootScale = (ScaleTransform)windowRoot.RenderTransform;
            startButton = Find<Button>("StartButton");
            startIcon = Find<TextBlock>("StartIcon");
            startText = Find<TextBlock>("StartText");
            statusText = Find<TextBlock>("StatusText");
            dot = Find<System.Windows.Shapes.Ellipse>("Dot");
            progressWrap = Find<FrameworkElement>("ProgressWrap");
            progressScale = (ScaleTransform)Find<Border>("ProgressFill").RenderTransform;
            segments = Find<UniformGrid>("Segments").Children.OfType<RadioButton>().ToArray();
            delaySegments = Find<UniformGrid>("DelaySegments").Children.OfType<RadioButton>().ToArray();
            customCount = Find<TextBox>("CustomCount");
            customHint = Find<TextBlock>("CustomHint");
            customSurface = Find<Border>("CustomSurface");
            mainPanel = Find<FrameworkElement>("MainPanel");
            aboutPanel = Find<FrameworkElement>("AboutPanel");
            settingsPanel = Find<FrameworkElement>("SettingsPanel");
            robloxVersionText = Find<TextBlock>("RobloxVersionText");
            versionDetailText = Find<TextBlock>("VersionDetailText");
            optTray = Find<CheckBox>("OptTray");
            optUpdate = Find<CheckBox>("OptUpdate");
            optNotify = Find<CheckBox>("OptNotify");
            currentPanel = mainPanel;

            // The Start button's colour fades between lime and red, so it gets its own brush
            startFill = new SolidColorBrush(StartColor);
            startButton.Background = startFill;
            startText0 = startButton.Foreground;

            CreateTray();
            RoundCorners();
            WireCountPicker();
            WireOptions();
            WireWindowButtons();

            string version = Assembly.GetExecutingAssembly()
                .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
                .Cast<AssemblyInformationalVersionAttribute>().Select(a => a.InformationalVersion).FirstOrDefault();
            Find<TextBlock>("VersionText").Text = "Version " + version;

            confirmTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            confirmTimeout.Tick += delegate { confirmTimeout.Stop(); confirming = false; Render(); };

            // Check for Roblox in the background so the window never freezes
            poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            poll.Tick += delegate { Check(); };
            poll.Start();

            // Look for a new Roblox now and every 30 minutes
            versionTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
            versionTimer.Tick += delegate { CheckVersion(); };
            versionTimer.Start();

            // Opening BloxNest again while it's running shows this window
            ThreadPool.RegisterWaitForSingleObject(showRequest,
                delegate { Window.Dispatcher.BeginInvoke(new Action(ShowFromTray)); }, null, -1, false);

            Render();
            RenderVersion();
            Window.Loaded += delegate { AnimateIn(); Check(); CheckVersion(); };
        }

        T Find<T>(string name) where T : class
        {
            return (T)Window.FindName(name);
        }

        static Brush Hex(string hex)
        {
            var b = (Brush)new BrushConverter().ConvertFromString(hex);
            b.Freeze();
            return b;
        }

        // Keeps the title bar's hover colours inside the rounded corners
        void RoundCorners()
        {
            var content = Find<FrameworkElement>("ContentRoot");
            content.SizeChanged += delegate
            {
                content.Clip = new RectangleGeometry(new Rect(content.RenderSize), 9, 9);
            };
        }

        // ---------- Window animations and the hidden icons ----------

        void AnimateIn()
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            windowRoot.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
            rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
            rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        }

        void AnimateOut(Action then)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
            fade.Completed += delegate { then(); };
            windowRoot.BeginAnimation(UIElement.OpacityProperty, fade);
            rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
            rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
        }

        void WireWindowButtons()
        {
            Find<Button>("MinButton").Click += delegate { AnimateOut(() => Window.WindowState = WindowState.Minimized); };
            Find<Button>("CloseButton").Click += delegate { CloseOrHide(); };
            Find<Button>("AboutButton").Click += delegate { ShowPanel(currentPanel == aboutPanel ? mainPanel : aboutPanel); };
            Find<Button>("SettingsButton").Click += delegate { ShowPanel(currentPanel == settingsPanel ? mainPanel : settingsPanel); };
            Find<Button>("BackButton").Click += delegate { ShowPanel(mainPanel); };
            Find<Button>("SettingsBackButton").Click += delegate { ShowPanel(mainPanel); };
            Find<Button>("CheckNowButton").Click += delegate { CheckVersion(); };
            startButton.Click += delegate { OnStart(); };

            Window.StateChanged += delegate { if (Window.WindowState == WindowState.Normal) AnimateIn(); };
            Window.Closing += (sender, e) =>
            {
                // Alt+F4 and the like: same as the close button
                if (quitting || !settings.Tray) return;
                e.Cancel = true;
                if (!hiding) CloseOrHide();
            };
            Window.Closed += delegate
            {
                if (tray != null) { tray.Visible = false; tray.Dispose(); }
            };
        }

        // Close button: hide to the hidden icons, or quit if that option is off
        void CloseOrHide()
        {
            if (!settings.Tray) { Quit(); return; }
            hiding = true;
            AnimateOut(() =>
            {
                Window.Hide();
                hiding = false;
                ShowPanel(mainPanel, false);
                if (!settings.TrayTipShown)
                {
                    tray.ShowBalloonTip(5000, "BloxNest is still running",
                        "It's in your hidden icons. Right-click it to quit.", Forms.ToolTipIcon.None);
                    settings.TrayTipShown = true;
                    settings.Save();
                }
            });
        }

        void ShowFromTray()
        {
            Window.Show();
            if (Window.WindowState == WindowState.Minimized) Window.WindowState = WindowState.Normal;
            Window.Activate();
            AnimateIn();
        }

        void Quit()
        {
            if (quitting) return;
            quitting = true;
            if (tray != null) tray.Visible = false;
            if (Window.IsVisible)
                AnimateOut(() => Application.Current.Shutdown());
            else
                Application.Current.Shutdown();
        }

        void CreateTray()
        {
            tray = new Forms.NotifyIcon { Text = "BloxNest" };
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("icon.ico"))
                tray.Icon = new Drawing.Icon(s, Forms.SystemInformation.SmallIconSize);

            var menu = new Forms.ContextMenuStrip
            {
                Renderer = new Forms.ToolStripProfessionalRenderer(new TrayMenuColors()) { RoundedEdges = false },
                ShowImageMargin = false,
                Font = new Drawing.Font("Segoe UI", 9.5f),
                Padding = new Forms.Padding(2, 4, 2, 4),
            };
            menu.Items.Add(TrayItem("Open BloxNest", true, delegate { ShowFromTray(); }));
            menu.Items.Add(TrayItem("Check for Roblox update", false, delegate { CheckVersion(); }));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(TrayItem("Quit BloxNest", false, delegate { Quit(); }));
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (sender, e) => { if (e.Button == Forms.MouseButtons.Left) ShowFromTray(); };
            tray.BalloonTipClicked += delegate { ShowFromTray(); };
            tray.Visible = true;
        }

        static Forms.ToolStripMenuItem TrayItem(string text, bool bold, EventHandler onClick)
        {
            var item = new Forms.ToolStripMenuItem(text)
            {
                ForeColor = Drawing.Color.FromArgb(0xEE, 0xF1, 0xE9),
                Padding = new Forms.Padding(6, 5, 18, 5),
            };
            if (bold) item.Font = new Drawing.Font("Segoe UI Semibold", 9.5f);
            item.Click += onClick;
            return item;
        }

        // Swaps the main view, About and Options with a short slide + fade
        void ShowPanel(FrameworkElement panel, bool animate = true)
        {
            if (panel == currentPanel) return;
            foreach (FrameworkElement p in new[] { mainPanel, aboutPanel, settingsPanel })
                p.Visibility = p == panel ? Visibility.Visible : Visibility.Collapsed;
            currentPanel = panel;
            if (panel == settingsPanel) RenderVersion();
            if (!animate) return;

            var slide = new TranslateTransform(panel == mainPanel ? -14 : 14, 0);
            panel.RenderTransform = slide;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
            panel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        }

        // ---------- Window count: 1-5 or a typed number ----------

        void WireCountPicker()
        {
            radioCount = settings.Count;
            segments[radioCount - 1].IsChecked = true;
            foreach (RadioButton rb in segments)
                rb.Checked += (sender, e) =>
                {
                    radioCount = int.Parse((string)((RadioButton)sender).Tag);
                    customCount.Text = "";
                    settings.Count = radioCount;
                    settings.Custom = 0;
                    settings.Save();
                    Render();
                };

            customCount.PreviewTextInput += (sender, e) => { e.Handled = !e.Text.All(char.IsDigit); };
            DataObject.AddPastingHandler(customCount, (sender, e) =>
            {
                string text = e.DataObject.GetData(typeof(string)) as string;
                if (text == null || !text.All(char.IsDigit)) e.CancelCommand();
            });
            customCount.TextChanged += delegate
            {
                int n;
                if (int.TryParse(customCount.Text, out n))
                {
                    custom = n;
                    foreach (RadioButton rb in segments) rb.IsChecked = false;
                    if (n >= 1 && n <= MaxWindows) { settings.Custom = n; settings.Save(); }
                }
                else custom = null;
                Render();
            };
            customCount.GotKeyboardFocus += delegate { Render(); };
            customCount.LostKeyboardFocus += delegate
            {
                // Left empty: go back to the last 1-5 choice
                if (custom == null && segments.All(rb => rb.IsChecked != true))
                    segments[radioCount - 1].IsChecked = true;
                Render();
            };
            customCount.KeyDown += (sender, e) => { if (e.Key == Key.Enter) OnStart(); };

            if (settings.Custom >= 1 && settings.Custom <= MaxWindows)
                customCount.Text = settings.Custom.ToString();
        }

        // ---------- Options ----------

        void WireOptions()
        {
            optTray.IsChecked = settings.Tray;
            optUpdate.IsChecked = settings.Update;
            optNotify.IsChecked = settings.Notify;
            foreach (RadioButton rb in delaySegments)
                rb.IsChecked = (string)rb.Tag == settings.Delay.ToString();

            RoutedEventHandler save = delegate
            {
                settings.Tray = optTray.IsChecked == true;
                settings.Update = optUpdate.IsChecked == true;
                settings.Notify = optNotify.IsChecked == true;
                RadioButton delay = delaySegments.FirstOrDefault(rb => rb.IsChecked == true);
                if (delay != null) settings.Delay = int.Parse((string)delay.Tag);
                settings.Save();
                RenderVersion();
            };
            foreach (CheckBox box in new[] { optTray, optUpdate, optNotify })
            {
                box.Checked += save;
                box.Unchecked += save;
            }
            foreach (RadioButton rb in delaySegments) rb.Checked += save;
        }

        // ---------- Roblox version ----------

        async void CheckVersion()
        {
            if (versionChecking) return;
            versionChecking = true;
            RenderVersion();
            RobloxVersion latest = await Task.Run(() => Roblox.GetLatest());
            versionChecking = false;
            if (latest != null)
            {
                latestKnown = latest;
                lastChecked = DateTime.Now;

                // A new Roblox is out: say so once per version
                bool behind = Roblox.PlayerIn(latest) == null && Roblox.FindPlayer() != null;
                if (behind && settings.Notify && settings.NotifiedVersion != latest.Version)
                {
                    tray.ShowBalloonTip(6000, "Roblox " + latest.ShortVersion + " is out",
                        settings.Update ? "BloxNest will update Roblox the next time you press Start."
                                        : "Open BloxNest to update Roblox.",
                        Forms.ToolTipIcon.None);
                    settings.NotifiedVersion = latest.Version;
                    settings.Save();
                }
            }
            RenderVersion();
        }

        void RenderVersion()
        {
            string installed = Roblox.ShortVersionOf(Roblox.FindPlayer());
            bool upToDate = latestKnown != null && Roblox.PlayerIn(latestKnown) != null;
            string line, detail;
            Brush color = Muted;

            if (installed == null)
            {
                line = "Roblox not installed";
                detail = "Install Roblox from roblox.com.";
            }
            else if (latestKnown == null)
            {
                line = "Roblox " + installed;
                detail = "You have Roblox " + installed + ". " + (versionChecking ? "Checking for updates..." : "Couldn't reach Roblox to check for updates.");
            }
            else if (upToDate)
            {
                line = "Roblox " + latestKnown.ShortVersion + " \u00B7 up to date";
                detail = "You have the newest Roblox (" + latestKnown.ShortVersion + "). Checked at " + lastChecked.ToShortTimeString() + ".";
            }
            else
            {
                line = "Roblox " + latestKnown.ShortVersion + " available";
                color = Lime;
                detail = "You have " + installed + ", newest is " + latestKnown.ShortVersion + ". "
                    + (settings.Update ? "It updates when you press Start." : "Turn on \"Update Roblox before opening\" to update it.");
            }
            if (versionChecking && latestKnown != null) detail = "Checking for updates...";

            robloxVersionText.Text = line;
            robloxVersionText.Foreground = color;
            versionDetailText.Text = detail;
        }

        // ---------- Opening windows ----------

        async void Check()
        {
            if (checking) return;
            checking = true;
            open = await Task.Run(() => Roblox.CountOpen());
            checking = false;
            if (open == 0) launchedByUs = false;
            Render();
        }

        async void OnStart()
        {
            if (busy != null || open < 0 || !CountValid) return;

            if (open > 0)
            {
                // Closing Roblox ends whatever game is running, so ask for a second click
                if (!confirming)
                {
                    confirming = true;
                    confirmTimeout.Start();
                    Render();
                    return;
                }
                confirming = false;
                confirmTimeout.Stop();
                busy = "closing";
                Render();
                await Task.Run(() => Roblox.CloseAll());
                open = 0;
            }

            error = null;
            launchedByUs = false;
            requested = Count;

            string exe = await PrepareRoblox();
            RenderVersion();
            if (exe == null)
            {
                busy = null;
                if (error == null)
                    SetError("Couldn't find Roblox. Install it from roblox.com, then try again.", Red);
                Render();
                return;
            }

            busy = "opening";
            Render();
            int n = requested, delay = settings.Delay;
            int code = await Task.Run(() => Roblox.LaunchWindows(n, exe, delay));

            open = await Task.Run(() => Roblox.CountOpen());
            busy = null;
            if (code == Roblox.ExitStillOpen)
                SetError("Roblox is still shutting down. Give it a few seconds and try again.", Amber);
            else if (open < n)
                SetError("Only " + open + " of " + n + " windows opened. Close Roblox and try again.", Red);
            else
                launchedByUs = true;
            Render();
        }

        // Makes sure the newest Roblox is installed (if that option is on) and returns its
        // path. Uses the installed version if Roblox's server can't be reached.
        async Task<string> PrepareRoblox()
        {
            string installed = Roblox.FindPlayer();
            if (!settings.Update)
            {
                runningVersion = Roblox.ShortVersionOf(installed);
                return installed;
            }

            busy = "checking";
            Render();
            RobloxVersion latest = await Task.Run(() => Roblox.GetLatest());
            if (latest == null)
            {
                runningVersion = Roblox.ShortVersionOf(installed);
                return installed;
            }
            latestKnown = latest;
            lastChecked = DateTime.Now;

            string exe = Roblox.PlayerIn(latest);
            if (exe != null)
            {
                runningVersion = latest.ShortVersion + " (latest)";
                return exe;
            }

            // Out of date: run the updater Roblox keeps on the PC
            string updater = Roblox.FindUpdater();
            if (updater == null || !await Task.Run(() => Signature.IsSignedByRoblox(updater)))
            {
                SetError("Roblox " + latest.ShortVersion + " is out. Open Roblox once from roblox.com so it can update, then try again.", Amber);
                return null;
            }
            busy = "updating";
            updateText = "Updating Roblox to " + latest.ShortVersion + "... a Roblox window may pop up.";
            Render();
            await Task.Run(() => Roblox.RunUpdater(updater));

            exe = Roblox.PlayerIn(latest);
            if (exe != null)
            {
                runningVersion = latest.ShortVersion + " (just updated)";
                return exe;
            }
            SetError("Roblox couldn't update to " + latest.ShortVersion + ". Open Roblox once from roblox.com, then try again.", Red);
            return null;
        }

        void SetError(string text, Brush color)
        {
            error = text;
            errorColor = color;
        }

        void Render()
        {
            bool idle = busy == null;
            foreach (RadioButton rb in segments) rb.IsEnabled = idle;
            customCount.IsEnabled = idle;
            RenderCustomBox();

            int count = Count;
            string status, icon = PlayIcon, label = "Start";
            Brush dotColor;
            double progress = -1;

            if (busy == "closing")
            {
                status = "Closing Roblox...";
                dotColor = Amber;
                icon = WaitIcon;
                label = "Closing Roblox...";
            }
            else if (busy == "checking")
            {
                status = "Checking for Roblox updates...";
                dotColor = Blue;
                icon = WaitIcon;
                label = "Checking...";
            }
            else if (busy == "updating")
            {
                status = updateText;
                dotColor = Blue;
                icon = WaitIcon;
                label = "Updating Roblox...";
            }
            else if (busy == "opening")
            {
                int ready = Math.Max(0, Math.Min(open, requested));
                status = "Opening windows... " + ready + " of " + requested + " open.";
                dotColor = Blue;
                icon = WaitIcon;
                label = "Opening...";
                progress = Math.Max(0.06, (double)ready / requested);
            }
            else if (open < 0)
            {
                status = "Checking for Roblox...";
                dotColor = Gray;
            }
            else if (!CountValid)
            {
                status = "Type a number from 1 to " + MaxWindows + ".";
                dotColor = Amber;
            }
            else if (error != null)
            {
                status = error;
                dotColor = errorColor;
                label = "Try again";
            }
            else if (open == 0)
            {
                status = "Ready to open " + Plural(count, "Roblox window") + "."
                    + (count > 8 ? " That many can slow your PC down." : "");
                dotColor = Blue;
            }
            else if (launchedByUs)
            {
                status = Plural(open, "Roblox window") + " running"
                    + (runningVersion != null ? " on Roblox " + runningVersion : "") + ". Have fun!";
                dotColor = Green;
            }
            else
            {
                status = "Roblox is already open. Starting will close it first.";
                dotColor = Amber;
            }

            // With Roblox open, Start restarts it with the chosen number of windows
            bool danger = false;
            if (idle && open > 0)
            {
                if (confirming)
                {
                    icon = WarnIcon;
                    label = "Click again to close Roblox";
                    danger = true;
                }
                else
                {
                    icon = RestartIcon;
                    label = "Close Roblox & open " + count;
                }
            }

            statusText.Text = status;
            dot.Fill = dotColor;
            startIcon.Text = icon;
            startText.Text = label;
            startButton.Foreground = danger ? Brushes.White : startText0;
            startButton.IsEnabled = idle && open >= 0 && CountValid;
            if (danger != dangerShown)
            {
                dangerShown = danger;
                startFill.BeginAnimation(SolidColorBrush.ColorProperty,
                    new ColorAnimation(danger ? DangerColor : StartColor, TimeSpan.FromMilliseconds(180)));
            }

            tray.Text = open > 0 ? "BloxNest - " + Plural(open, "Roblox window") + " open" : "BloxNest";
            SetPulse(!idle);
            SetProgress(progress);
        }

        // Highlights the Custom box like a selected segment while it holds a valid number
        void RenderCustomBox()
        {
            bool hasNumber = custom != null;
            bool valid = hasNumber && CountValid;
            customSurface.Background = valid ? SelectedSurface : Brushes.Transparent;
            customCount.Foreground = valid ? SelectedText : hasNumber ? Red : LightText;
            customCount.CaretBrush = valid ? SelectedText : Lime;
            customHint.Visibility = !hasNumber && !customCount.IsKeyboardFocused ? Visibility.Visible : Visibility.Collapsed;
        }

        static string Plural(int n, string noun)
        {
            return n + " " + noun + (n == 1 ? "" : "s");
        }

        void SetPulse(bool on)
        {
            if (on == pulsing) return;
            pulsing = on;
            if (on)
                dot.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromSeconds(0.7))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                });
            else
                dot.BeginAnimation(UIElement.OpacityProperty, null);
        }

        void SetProgress(double value)
        {
            progressWrap.Visibility = value < 0 ? Visibility.Collapsed : Visibility.Visible;
            if (value < 0)
            {
                shownProgress = -1;
                progressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                progressScale.ScaleX = 0;
                return;
            }
            if (value == shownProgress) return;
            shownProgress = value;
            progressScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(value, TimeSpan.FromMilliseconds(450)) { EasingFunction = new CubicEase() });
        }
    }
}
