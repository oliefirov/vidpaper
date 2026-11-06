using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

static class Native
{
    public delegate bool EnumProc(IntPtr h, IntPtr p);

    [DllImport("user32.dll")] public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int idx);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int idx, int val);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool SystemParametersInfo(uint action, uint param, string data, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint count);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    public delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int L, T, R, B; }

    public static string ClassOf(IntPtr h)
    {
        var sb = new StringBuilder(128);
        GetClassName(h, sb, 128);
        return sb.ToString();
    }
}

static class Program
{
    [STAThread]
    static void Main()
    {
        bool first;
        using (var mutex = new System.Threading.Mutex(true, "VidPaper_SingleInstance", out first))
        {
            if (!first) return;

            string ffplay = FindTool("ffplay.exe");
            string ffmpeg = FindTool("ffmpeg.exe");
            if (ffplay == null || ffmpeg == null)
            {
                MessageBox.Show("ffmpeg.exe and ffplay.exe were not both found.\nPut them next to VidPaper.exe or add FFmpeg's bin folder to PATH.",
                                "VidPaper", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e) { CrashLog(e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { CrashLog(e.ExceptionObject); };
            Application.Run(new App(ffplay, ffmpeg));
        }
    }

    static void CrashLog(object ex)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VidPaper");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "log.txt"), DateTime.Now.ToString("HH:mm:ss.fff") + "  CRASH: " + ex + "\r\n");
        }
        catch { }
    }

    static string FindTool(string name)
    {
        string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name);
        if (File.Exists(local)) return local;

        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string dir in path.Split(';'))
        {
            try
            {
                string p = Path.Combine(dir.Trim().Trim('"'), name);
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }
}

class App : ApplicationContext
{
    const string Title = "VidPaperPlayer";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const int WS_POPUP = unchecked((int)0x80000000);
    const int WS_CHILD = 0x40000000;
    const int WS_VISIBLE = 0x10000000;
    const int WS_STRIP = WS_POPUP | 0x00CF0000;
    static readonly IntPtr HWND_BOTTOM = (IntPtr)1;
    static readonly object logLock = new object();

    readonly string ffplay, ffmpeg;
    readonly string configFile, settingsFile, logFile;
    readonly NotifyIcon tray = new NotifyIcon();
    readonly Timer attachTimer = new Timer();
    readonly Timer watchTimer = new Timer();
    ToolStripMenuItem pauseItem, startupItem, altItem, autoItem, rendererMenu;
    string renderer = "";
    bool autoPause = true, postLogged;
    int ffLines;
    DateTime attachedAt;
    Native.WinEventProc winEventProc;
    IntPtr winHook;
    System.Threading.ManualResetEvent gate;
    bool paused;

    Process proc, feeder;
    string video;
    bool attached, userPaused, alt;
    int tries, fails, activeMode, zRepairs;
    IntPtr playerWnd;

    public App(string ffplayPath, string ffmpegPath)
    {
        ffplay = ffplayPath;
        ffmpeg = ffmpegPath;
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VidPaper");
        Directory.CreateDirectory(dir);
        configFile = Path.Combine(dir, "video.txt");
        settingsFile = Path.Combine(dir, "settings.ini");
        logFile = Path.Combine(dir, "log.txt");
        try { if (File.Exists(logFile) && new FileInfo(logFile).Length > 200000) File.WriteAllText(logFile, ""); } catch { }
        Log("---- VidPaper started. ffplay: " + ffplay + ", ffmpeg: " + ffmpeg);

        alt = GetSetting("alt", "0") == "1";
        autoPause = GetSetting("autopause", "1") == "1";
        renderer = GetSetting("renderer", "direct3d11");

        var menu = new ContextMenuStrip();
        menu.Items.Add("Choose video...", null, delegate { Choose(); });
        pauseItem = new ToolStripMenuItem("Pause", null, delegate
        {
            userPaused = !userPaused;
            pauseItem.Checked = userPaused;
            Watch(null, null);
        });
        menu.Items.Add(pauseItem);
        altItem = new ToolStripMenuItem("Alternate desktop mode (try if nothing shows)", null, delegate
        {
            alt = !alt;
            altItem.Checked = alt;
            SetSetting("alt", alt ? "1" : "0");
            Log("Alternate mode = " + alt);
            if (video != null) { fails = 0; Play(video); }
        });
        altItem.Checked = alt;
        menu.Items.Add(altItem);

        autoItem = new ToolStripMenuItem("Pause when a window covers the desktop", null, delegate
        {
            autoPause = !autoPause;
            autoItem.Checked = autoPause;
            SetSetting("autopause", autoPause ? "1" : "0");
            Log("Auto-pause = " + autoPause);
            Watch(null, null);
        });
        autoItem.Checked = autoPause;
        menu.Items.Add(autoItem);

        rendererMenu = new ToolStripMenuItem("Renderer (try if nothing shows)");
        foreach (string rn in new string[] { "", "software", "direct3d11", "direct3d", "opengl" })
        {
            string name = rn;
            var it = new ToolStripMenuItem(name == "" ? "SDL default" : name, null, delegate
            {
                renderer = name;
                SetSetting("renderer", name);
                foreach (ToolStripMenuItem o in rendererMenu.DropDownItems)
                    o.Checked = (o.Text == (name == "" ? "SDL default" : name));
                Log("Renderer = " + (name == "" ? "SDL default" : name));
                if (video != null) { fails = 0; Play(video); }
            });
            it.Checked = (name == renderer);
            rendererMenu.DropDownItems.Add(it);
        }
        menu.Items.Add(rendererMenu);
        startupItem = new ToolStripMenuItem("Run at Windows startup", null, delegate
        {
            SetStartup(!startupItem.Checked);
            startupItem.Checked = IsStartupOn();
        });
        startupItem.Checked = IsStartupOn();
        menu.Items.Add(startupItem);
        menu.Items.Add("Open log", null, delegate
        {
            try { Process.Start("notepad.exe", "\"" + logFile + "\""); } catch { }
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, delegate { Quit(); });

        tray.Icon = LoadTrayIcon();
        tray.Text = "VidPaper";
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += delegate { Choose(); };
        tray.Visible = true;

        attachTimer.Interval = 100;
        attachTimer.Tick += TryAttach;
        watchTimer.Interval = 500;
        watchTimer.Tick += Watch;
        watchTimer.Start();
        winEventProc = delegate(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time) { Watch(null, null); };
        winHook = Native.SetWinEventHook(0x0003, 0x0017, IntPtr.Zero, winEventProc, 0, 0, 0);

        string saved = null;
        try { if (File.Exists(configFile)) saved = File.ReadAllText(configFile).Trim(); } catch { }
        if (!string.IsNullOrEmpty(saved) && File.Exists(saved)) Play(saved);
        else Choose();
    }

    static Icon LoadTrayIcon()
    {
        try
        {
            IntPtr[] small = new IntPtr[1];
            if (Native.ExtractIconEx(Application.ExecutablePath, 0, null, small, 1) > 0 && small[0] != IntPtr.Zero)
                return Icon.FromHandle(small[0]);
        }
        catch { }
        return SystemIcons.Application;
    }

    string GetSetting(string key, string def)
    {
        try
        {
            if (File.Exists(settingsFile))
                foreach (string line in File.ReadAllLines(settingsFile))
                {
                    int i = line.IndexOf('=');
                    if (i > 0 && line.Substring(0, i) == key) return line.Substring(i + 1);
                }
        }
        catch { }
        return def;
    }

    void SetSetting(string key, string val)
    {
        try
        {
            var lines = new List<string>();
            if (File.Exists(settingsFile)) lines.AddRange(File.ReadAllLines(settingsFile));
            bool done = false;
            for (int i = 0; i < lines.Count; i++)
                if (lines[i].StartsWith(key + "=")) { lines[i] = key + "=" + val; done = true; }
            if (!done) lines.Add(key + "=" + val);
            File.WriteAllLines(settingsFile, lines.ToArray());
        }
        catch { }
    }

    void Log(string s)
    {
        try
        {
            lock (logLock)
                File.AppendAllText(logFile, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + "\r\n");
        }
        catch { }
    }

    void Balloon(string msg)
    {
        try { tray.ShowBalloonTip(6000, "VidPaper", msg, ToolTipIcon.Error); } catch { }
    }

    void Choose()
    {
        using (var d = new OpenFileDialog())
        {
            d.Title = "Choose a video wallpaper";
            d.Filter = "Video|*.mp4;*.webm;*.mkv;*.mov;*.avi;*.gif;*.m4v|All files|*.*";
            if (d.ShowDialog() != DialogResult.OK) return;
            try { File.WriteAllText(configFile, d.FileName); } catch { }
            fails = 0;
            Play(d.FileName);
        }
    }

    void Quit()
    {
        watchTimer.Stop();
        if (winHook != IntPtr.Zero) Native.UnhookWinEvent(winHook);
        video = null;
        Kill();
        tray.Visible = false;
        Native.SystemParametersInfo(20, 0, null, 0);
        ExitThread();
    }

    void Play(string path)
    {
        Kill();
        if (!File.Exists(path)) { Log("File not found: " + path); return; }
        video = path;

        Rectangle b = Screen.PrimaryScreen.Bounds;
        string playArgs = string.Format(
            "-loglevel warning -nostats -an -sn -noborder -framedrop -left 30000 -top 30000 " +
            "-x {0} -y {1} -window_title {2} " +
            "-vf \"scale={0}:{1}:force_original_aspect_ratio=increase:flags=fast_bilinear,crop={0}:{1}\" -i pipe:0",
            b.Width, b.Height, Title);

        bool gif = path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
        string codec = gif ? "-vf format=yuvj420p -c:v mjpeg -q:v 2" : "-c:v copy";
        string feedArgs = string.Format(
            "-hide_banner -loglevel error -nostdin -stream_loop -1 -i \"{0}\" -map 0:v:0 {1} -an -sn -dn -f matroska -",
            path, codec);
        Log("Starting: ffmpeg " + feedArgs);
        Log("      |   ffplay " + playArgs);

        var psiPlay = new ProcessStartInfo(ffplay, playArgs);
        psiPlay.UseShellExecute = false;
        psiPlay.CreateNoWindow = true;
        psiPlay.RedirectStandardInput = true;
        psiPlay.RedirectStandardError = true;
        if (renderer != "") psiPlay.EnvironmentVariables["SDL_RENDER_DRIVER"] = renderer;
        Log("SDL renderer: " + (renderer == "" ? "default" : renderer));

        var psiFeed = new ProcessStartInfo(ffmpeg, feedArgs);
        psiFeed.UseShellExecute = false;
        psiFeed.CreateNoWindow = true;
        psiFeed.RedirectStandardOutput = true;
        psiFeed.RedirectStandardError = true;

        ffLines = 0;
        try
        {
            proc = Process.Start(psiPlay);
            feeder = Process.Start(psiFeed);
            proc.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
            {
                if (e.Data != null && ++ffLines <= 80) Log("ffplay: " + e.Data);
            };
            feeder.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
            {
                if (e.Data != null && ++ffLines <= 80) Log("ffmpeg: " + e.Data);
            };
            proc.BeginErrorReadLine();
            feeder.BeginErrorReadLine();
            try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            try { feeder.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

            gate = new System.Threading.ManualResetEvent(true);
            System.Threading.ManualResetEvent g = gate;
            Stream inStream = feeder.StandardOutput.BaseStream;
            Stream outStream = proc.StandardInput.BaseStream;
            var pump = new System.Threading.Thread(delegate() { Pump(inStream, outStream, g); });
            pump.IsBackground = true;
            pump.Start();
        }
        catch (Exception ex)
        {
            Log("Could not start ffmpeg/ffplay: " + ex.Message);
            Kill();
            video = null;
            MessageBox.Show("Could not start ffmpeg/ffplay:\n" + ex.Message, "VidPaper");
            return;
        }
        attached = false;
        paused = false;
        tries = 0;
        attachTimer.Start();
    }

    static void Pump(Stream src, Stream dst, System.Threading.ManualResetEvent gate)
    {
        byte[] buf = new byte[65536];
        try
        {
            int n;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
            {
                gate.WaitOne();
                dst.Write(buf, 0, n);
                dst.Flush();
            }
        }
        catch { }
        try { dst.Close(); } catch { }
    }

    void Kill()
    {
        attachTimer.Stop();
        attached = false;
        paused = false;
        playerWnd = IntPtr.Zero;
        if (gate != null) gate.Set();
        try { if (feeder != null && !feeder.HasExited) feeder.Kill(); } catch { }
        try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
        feeder = null;
        proc = null;
        gate = null;
    }

    void TryAttach(object sender, EventArgs e)
    {
        if (proc == null || proc.HasExited) { attachTimer.Stop(); return; }

        IntPtr hw = Native.FindWindow(null, Title);
        if (hw == IntPtr.Zero)
        {
            if (++tries > 100)
            {
                attachTimer.Stop();
                Log("ffplay window never appeared (ffplay is running but opened no window).");
                Balloon("ffplay started but showed no window. See log (tray menu > Open log).");
            }
            return;
        }
        attachTimer.Stop();
        Log("Found ffplay window " + hw + " after ~" + (tries * 100) + " ms");

        AttachToDesktop(hw);

        attached = true;
        attachedAt = DateTime.Now;
        postLogged = false;
        fails = 0;
    }

    void AttachToDesktop(IntPtr hw)
    {
        IntPtr progman = Native.FindWindow("Progman", null);
        IntPtr res;
        Native.SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0, 1000, out res);

        IntPtr defView = Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        IntPtr childWorker = Native.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
        bool newLayout = defView != IntPtr.Zero;

        IntPtr legacyWorker = IntPtr.Zero;
        if (!newLayout)
        {
            IntPtr found = IntPtr.Zero;
            Native.EnumWindows(delegate(IntPtr top, IntPtr p)
            {
                if (Native.FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                    found = Native.FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
                return true;
            }, IntPtr.Zero);
            legacyWorker = found;
        }

        int auto = newLayout ? 2 : 1;
        int mode = alt ? (auto == 1 ? 2 : 1) : auto;
        Log(string.Format("Layout: {0}, progman={1}, defViewInProgman={2}, childWorker={3}, legacyWorker={4}, mode={5}",
            newLayout ? "new (24H2+)" : "classic", progman, defView, childWorker, legacyWorker, mode));

        Rectangle b = Screen.PrimaryScreen.Bounds;
        Rectangle vs = SystemInformation.VirtualScreen;
        int x = b.X - vs.X, y = b.Y - vs.Y;

        if (mode == 1)
        {
            IntPtr host = newLayout ? childWorker : legacyWorker;
            if (host == IntPtr.Zero) host = progman;
            Native.SetParent(hw, host);
            SetChildStyle(hw);
            Native.SetWindowLong(hw, -20, Native.GetWindowLong(hw, -20) | 0x08000000 | 0x00000080);
            Native.SetWindowPos(hw, HWND_BOTTOM, x, y, b.Width, b.Height, 0x0010 | 0x0040 | 0x0020);
        }
        else
        {
            SetChildStyle(hw);
            int ex = Native.GetWindowLong(hw, -20);
            Native.SetWindowLong(hw, -20, ex | 0x00080000 | 0x00000020 | 0x08000000 | 0x00000080);
            Native.SetLayeredWindowAttributes(hw, 0, 255, 2);
            Native.SetParent(hw, progman);
            ZFix(progman, hw);
            Native.SetWindowPos(hw, IntPtr.Zero, x, y, b.Width, b.Height, 0x0004 | 0x0010 | 0x0040 | 0x0020);
        }

        playerWnd = hw;
        activeMode = mode;
        zRepairs = 0;

        Native.RECT r;
        Native.GetWindowRect(hw, out r);
        string zdump = "";
        bool zok = mode != 2 || ZOrderOk(progman, hw, out zdump);
        Log(string.Format("Attached. parent now {0} ({1}), visible={2}, rect=({3},{4})-({5},{6}), z-order ok: {7} [{8}]",
            Native.GetParent(hw), Native.ClassOf(Native.GetParent(hw)), Native.IsWindowVisible(hw),
            r.L, r.T, r.R, r.B, zok, zdump));
    }

    static void SetChildStyle(IntPtr hw)
    {
        int style = Native.GetWindowLong(hw, -16);
        style = (style & ~WS_STRIP) | WS_CHILD | WS_VISIBLE;
        Native.SetWindowLong(hw, -16, style);
    }

    static void ZFix(IntPtr progman, IntPtr hw)
    {
        const uint f = 0x0002 | 0x0001 | 0x0010;
        IntPtr defView = Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView != IntPtr.Zero) Native.SetWindowPos(hw, defView, 0, 0, 0, 0, f);
        IntPtr w = Native.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
        if (w != IntPtr.Zero) Native.SetWindowPos(w, hw, 0, 0, 0, 0, f);
    }

    static bool ZOrderOk(IntPtr progman, IntPtr me, out string dump)
    {
        int index = 0, iDef = -1, iMe = -1, iWorker = -1;
        var sb = new StringBuilder();
        for (IntPtr w = Native.GetWindow(progman, 5); w != IntPtr.Zero; w = Native.GetWindow(w, 2))
        {
            string c = Native.ClassOf(w);
            sb.Append(w == me ? "PLAYER" : c).Append(" > ");
            if (w == me) iMe = index;
            else if (c == "SHELLDLL_DefView" && iDef < 0) iDef = index;
            else if (c == "WorkerW" && iWorker < 0) iWorker = index;
            index++;
        }
        dump = sb.ToString();
        if (iMe < 0) return true;
        if (iDef >= 0 && iDef > iMe) return false;
        if (iWorker >= 0 && iWorker < iMe) return false;
        return true;
    }

    bool IsPaused()
    {
        return userPaused || (autoPause && DesktopCovered());
    }

    void Watch(object sender, EventArgs e)
    {
        try
        {
            if (video == null || proc == null || feeder == null) return;

            if (proc.HasExited || feeder.HasExited)
            {
                int c1 = -1, c2 = -1;
                try { c1 = proc.ExitCode; } catch { }
                try { c2 = feeder.ExitCode; } catch { }
                Log(string.Format("Playback stopped: ffplay exited={0} (code {1}), ffmpeg exited={2} (code {3}), attempt {4}",
                    proc.HasExited, c1, feeder.HasExited, c2, fails + 1));
                if (++fails <= 3) Play(video);
                else
                {
                    Balloon("ffmpeg/ffplay keep exiting. Try the file in CMD, and see the log.");
                    video = null;
                    Kill();
                }
                return;
            }

            bool wantPause = attached && IsPaused();
            if (wantPause != paused)
            {
                paused = wantPause;
                if (gate != null)
                {
                    if (paused) gate.Reset();
                    else gate.Set();
                }
                Log(paused ? "Paused (video data stopped, last frame stays)" : "Resumed");
            }

            if (attached && !postLogged && (DateTime.Now - attachedAt).TotalSeconds >= 3 && playerWnd != IntPtr.Zero)
            {
                postLogged = true;
                Native.RECT pr;
                Native.GetWindowRect(playerWnd, out pr);
                bool responding = false;
                try { responding = proc.Responding; } catch { }
                Log(string.Format("Status 3 s after attach: visible={0}, parent={1}, rect=({2},{3})-({4},{5}), responding={6}, foreground class={7}",
                    Native.IsWindowVisible(playerWnd), Native.ClassOf(Native.GetParent(playerWnd)), pr.L, pr.T, pr.R, pr.B,
                    responding, Native.ClassOf(Native.GetForegroundWindow())));
            }

            if (attached && activeMode == 2 && playerWnd != IntPtr.Zero)
            {
                IntPtr pm = Native.FindWindow("Progman", null);
                string dump;
                if (pm != IntPtr.Zero && pm == Native.GetParent(playerWnd) && !ZOrderOk(pm, playerWnd, out dump))
                {
                    if (zRepairs++ < 20) Log("Z-order was wrong, repairing: " + dump);
                    ZFix(pm, playerWnd);
                }
            }
        }
        catch (Exception ex)
        {
            Log("Watch error: " + ex.Message);
        }
    }

    static bool DesktopCovered()
    {
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;

        string c = Native.ClassOf(fg);
        if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd" ||
            c == "Windows.UI.Core.CoreWindow" || c == "NotifyIconOverflowWindow" ||
            c == "SDL_app" || c == "TopLevelWindowForOverflowXamlIsland" || c.StartsWith("WindowsForms10"))
            return false;

        Screen s = Screen.FromHandle(fg);
        if (!s.Primary) return false;
        if (Native.IsZoomed(fg)) return true;

        Native.RECT r;
        if (!Native.GetWindowRect(fg, out r)) return false;
        Rectangle b = s.Bounds;
        return r.L <= b.X && r.T <= b.Y && r.R >= b.Right && r.B >= b.Bottom;
    }

    static bool IsStartupOn()
    {
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
            return k != null && k.GetValue("VidPaper") != null;
    }

    static void SetStartup(bool on)
    {
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
        {
            if (k == null) return;
            if (on) k.SetValue("VidPaper", "\"" + Application.ExecutablePath + "\"");
            else k.DeleteValue("VidPaper", false);
        }
    }
}
