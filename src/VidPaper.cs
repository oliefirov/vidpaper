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
    [DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr h);
    [DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);

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

            string ffplay = FindFfplay();
            if (ffplay == null)
            {
                MessageBox.Show("ffplay.exe was not found.\nPut it next to VidPaper.exe or add FFmpeg's bin folder to PATH.",
                                "VidPaper", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.Run(new App(ffplay));
        }
    }

    static string FindFfplay()
    {
        string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffplay.exe");
        if (File.Exists(local)) return local;

        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string dir in path.Split(';'))
        {
            try
            {
                string p = Path.Combine(dir.Trim().Trim('"'), "ffplay.exe");
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

    readonly string ffplay;
    readonly string configFile, settingsFile, logFile;
    readonly NotifyIcon tray = new NotifyIcon();
    readonly Timer attachTimer = new Timer();
    readonly Timer watchTimer = new Timer();
    ToolStripMenuItem pauseItem, startupItem, altItem, autoItem, rendererMenu;
    string renderer = "";
    bool autoPause = true, postLogged;
    int ffLines;
    DateTime attachedAt;

    Process proc;
    string video;
    bool attached, suspended, userPaused, alt;
    int tries, fails, activeMode;
    IntPtr playerWnd, progmanWnd, defViewWnd;

    public App(string ffplayPath)
    {
        ffplay = ffplayPath;
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VidPaper");
        Directory.CreateDirectory(dir);
        configFile = Path.Combine(dir, "video.txt");
        settingsFile = Path.Combine(dir, "settings.ini");
        logFile = Path.Combine(dir, "log.txt");
        try { File.WriteAllText(logFile, ""); } catch { }
        Log("VidPaper started. ffplay: " + ffplay);

        alt = GetSetting("alt", "0") == "1";
        autoPause = GetSetting("autopause", "1") == "1";
        renderer = GetSetting("renderer", "direct3d11");

        var menu = new ContextMenuStrip();
        menu.Items.Add("Choose video...", null, delegate { Choose(); });
        pauseItem = new ToolStripMenuItem("Pause", null, delegate
        {
            userPaused = !userPaused;
            pauseItem.Checked = userPaused;
            ApplyPause();
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
            ApplyPause();
        });
        autoItem.Checked = autoPause;
        menu.Items.Add(autoItem);

        rendererMenu = new ToolStripMenuItem("Renderer");
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

        tray.Icon = SystemIcons.Application;
        tray.Text = "VidPaper";
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += delegate { Choose(); };
        tray.Visible = true;

        attachTimer.Interval = 100;
        attachTimer.Tick += TryAttach;
        watchTimer.Interval = 1500;
        watchTimer.Tick += Watch;
        watchTimer.Start();

        string saved = null;
        try { if (File.Exists(configFile)) saved = File.ReadAllText(configFile).Trim(); } catch { }
        if (!string.IsNullOrEmpty(saved) && File.Exists(saved)) Play(saved);
        else Choose();
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
        string args = string.Format(
            "-loglevel warning -nostats -an -sn -noborder -loop 0 -framedrop " +
            "-x {0} -y {1} -window_title {2} " +
            "-vf \"scale={0}:{1}:force_original_aspect_ratio=increase:flags=fast_bilinear,crop={0}:{1}\" -i \"{3}\"",
            b.Width, b.Height, Title, path);
        Log("Starting: ffplay " + args);

        var psi = new ProcessStartInfo(ffplay, args);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardError = true;
        if (renderer != "") psi.EnvironmentVariables["SDL_RENDER_DRIVER"] = renderer;
        Log("SDL renderer: " + (renderer == "" ? "default" : renderer));
        ffLines = 0;
        try
        {
            proc = Process.Start(psi);
            proc.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
            {
                if (e.Data != null && ++ffLines <= 80) Log("ffplay: " + e.Data);
            };
            proc.BeginErrorReadLine();
            try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        }
        catch (Exception ex)
        {
            Log("Could not start ffplay: " + ex.Message);
            MessageBox.Show("Could not start ffplay:\n" + ex.Message, "VidPaper");
            video = null;
            return;
        }
        attached = false;
        suspended = false;
        tries = 0;
        attachTimer.Start();
    }

    void Kill()
    {
        attachTimer.Stop();
        attached = false;
        suspended = false;
        playerWnd = IntPtr.Zero;
        try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
        proc = null;
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
        ApplyPause();
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
            Native.SetWindowPos(hw, HWND_BOTTOM, x, y, b.Width, b.Height, 0x0010 | 0x0040 | 0x0020);
        }
        else
        {
            SetChildStyle(hw);
            int ex = Native.GetWindowLong(hw, -20);
            Native.SetWindowLong(hw, -20, ex | 0x00080000);
            Native.SetLayeredWindowAttributes(hw, 0, 255, 2);
            Native.SetParent(hw, progman);
            ZFix(progman, defView, hw);
            Native.SetWindowPos(hw, IntPtr.Zero, x, y, b.Width, b.Height, 0x0004 | 0x0010 | 0x0040 | 0x0020);
        }

        playerWnd = hw;
        progmanWnd = progman;
        defViewWnd = defView;
        activeMode = mode;

        Native.RECT r;
        Native.GetWindowRect(hw, out r);
        Log(string.Format("Attached. parent now {0} ({1}), visible={2}, rect=({3},{4})-({5},{6}), WorkerW above player: {7}",
            Native.GetParent(hw), Native.ClassOf(Native.GetParent(hw)), Native.IsWindowVisible(hw),
            r.L, r.T, r.R, r.B, mode == 2 && WorkerAboveMe(progman, hw)));
    }

    static void SetChildStyle(IntPtr hw)
    {
        int style = Native.GetWindowLong(hw, -16);
        style = (style & ~WS_STRIP) | WS_CHILD | WS_VISIBLE;
        Native.SetWindowLong(hw, -16, style);
    }

    static void ZFix(IntPtr progman, IntPtr defView, IntPtr hw)
    {
        const uint f = 0x0002 | 0x0001 | 0x0010;
        if (defView != IntPtr.Zero) Native.SetWindowPos(hw, defView, 0, 0, 0, 0, f);
        IntPtr w = Native.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
        if (w != IntPtr.Zero) Native.SetWindowPos(w, hw, 0, 0, 0, 0, f);
    }

    static bool WorkerAboveMe(IntPtr progman, IntPtr me)
    {
        for (IntPtr w = Native.GetWindow(progman, 5); w != IntPtr.Zero; w = Native.GetWindow(w, 2))
        {
            if (w == me) return false;
            if (Native.ClassOf(w) == "WorkerW") return true;
        }
        return false;
    }

    void Watch(object sender, EventArgs e)
    {
        if (video != null && (proc == null || proc.HasExited))
        {
            int code = -1;
            try { if (proc != null) code = proc.ExitCode; } catch { }
            Log("ffplay is not running (exit code " + code + "), attempt " + (fails + 1));
            if (++fails <= 3) Play(video);
            else
            {
                Balloon("ffplay keeps exiting (code " + code + "). Try the file in CMD, and see the log.");
                video = null;
            }
            return;
        }
        if (attached && !postLogged && (DateTime.Now - attachedAt).TotalSeconds >= 3 && playerWnd != IntPtr.Zero)
        {
            postLogged = true;
            Native.RECT pr;
            Native.GetWindowRect(playerWnd, out pr);
            bool responding = false;
            try { responding = proc.Responding; } catch { }
            Log(string.Format("Status 3 s after attach: visible={0}, parent={1}, rect=({2},{3})-({4},{5}), suspended={6}, responding={7}, foreground class={8}",
                Native.IsWindowVisible(playerWnd), Native.ClassOf(Native.GetParent(playerWnd)), pr.L, pr.T, pr.R, pr.B,
                suspended, responding, Native.ClassOf(Native.GetForegroundWindow())));
        }
        if (attached && activeMode == 2 && playerWnd != IntPtr.Zero && WorkerAboveMe(progmanWnd, playerWnd))
        {
            Log("WorkerW moved above the player; fixing z-order");
            ZFix(progmanWnd, defViewWnd, playerWnd);
        }
        ApplyPause();
    }

    void ApplyPause()
    {
        if (!attached || proc == null || proc.HasExited) return;
        bool want = userPaused || (autoPause && DesktopCovered());
        if (want == suspended) return;
        try
        {
            if (want) Native.NtSuspendProcess(proc.Handle);
            else Native.NtResumeProcess(proc.Handle);
            suspended = want;
            Log((want ? "Suspended ffplay" : "Resumed ffplay") + " (foreground class: " + Native.ClassOf(Native.GetForegroundWindow()) + ")");
        }
        catch { }
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
