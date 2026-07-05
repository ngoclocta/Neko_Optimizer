using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.Win32;
using System.Windows.Forms;
using System.Drawing;
using System.Drawing.Drawing2D;

// ============================================================
// Neko Cpu Optimizer v1.0
// Fix 1: Whitelist phân loại theo AppType (Audio/Messenger/Browser/Normal)
// Fix 2: Khởi động cùng Windows qua Task Scheduler (không cần UAC)
// Fix 3: Bảo vệ tiến trình Windows qua path-guard + DoNotTouch list
// Fix 4: System Tray Icon — hiển thị thông tin khi ẩn cửa sổ
// ============================================================

enum PriorityTier {
    TIER_1_REALTIME = 1,
    TIER_2_NORMAL   = 2,
    TIER_3_LOW      = 3,
    TIER_4_FROZEN   = 4
}

enum AppType {
    Normal,
    Browser,
    Audio,
    Messenger
}

class WhitelistEntry {
    public string Name;
    public AppType Type;
    public int WakeIntervalSeconds;
}

class ProcessState {
    public uint     Pid;
    public string   Name;
    public PriorityTier Tier;
    public DateTime LastStateChangeTime;
    public DateTime EnteredBackgroundTime;
    public DateTime EnteredSuspendedTime;
    public bool     IsSuspended;
    public bool     IsRamThrottled;
    public bool     IsTemporarilyPromoted;
    public DateTime PromotionStartTime;
    public long     LastKernelTime;
    public long     LastUserTime;
    public DateTime LastCpuCheckTime;
    public double   CpuUsagePercent;
    public int      IdleCycles;
    public int      LowLoadCycles;
    public DateTime LastWakeTime;
}

class Program {
    // ===================== Win32 API =====================
    [DllImport("ntdll.dll", SetLastError = true)]
    static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll", SetLastError = true)]
    static extern int NtResumeProcess(IntPtr processHandle);

    [DllImport("psapi.dll", SetLastError = true)]
    static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetPriorityClass(IntPtr handle, uint priorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessAffinityMask(IntPtr handle, IntPtr affinityMask);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("shell32.dll", EntryPoint = "IsUserAnAdmin")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool IsUserAnAdmin();

    [DllImport("kernel32.dll")]
    static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll")]
    static extern bool AllocConsole();

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    static extern bool SetConsoleTitle(string lpConsoleTitle);

    [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int _kbhit();

    [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int _getch();

    [DllImport("kernel32.dll")]
    static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate handler, bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessTimes(IntPtr hProcess,
        out long lpCreationTime, out long lpExitTime,
        out long lpKernelTime,   out long lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetSystemTimes(out long lpIdleTime,
        out long lpKernelTime, out long lpUserTime);

    delegate bool ConsoleCtrlDelegate(int ctrlType);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct MEMORYSTATUSEX {
        public uint  dwLength;
        public uint  dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    // ===================== Chrome process tree (Fix Chrome crash) =====================
    const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct PROCESSENTRY32 {
        public uint  dwSize;
        public uint  cntUsage;
        public uint  th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint  th32ModuleID;
        public uint  cntThreads;
        public uint  th32ParentProcessID;
        public int   pcPriClassBase;
        public uint  dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    // ===================== Constants =====================
    const uint PROCESS_SUSPEND_RESUME           = 0x0800;
    const uint PROCESS_SET_INFORMATION          = 0x0200;
    const uint PROCESS_SET_QUOTA                = 0x0100;
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    const uint HIGH_PRIORITY_CLASS         = 0x00000080;
    const uint NORMAL_PRIORITY_CLASS       = 0x00000020;
    const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;
    const uint IDLE_PRIORITY_CLASS         = 0x00000040;

    const int SW_HIDE    = 0;
    const int SW_SHOW    = 5;
    const int SW_RESTORE = 9;

    const string TASK_NAME = "Neko_Cpu_Optimizer";

    // ===================== Global State =====================
    static Dictionary<string, WhitelistEntry> g_UserWhitelist =
        new Dictionary<string, WhitelistEntry>(StringComparer.OrdinalIgnoreCase);
    static Dictionary<uint, ProcessState> g_ProcessStates = new Dictionary<uint, ProcessState>();

    // Chrome process tree cache — cập nhật mỗi tick để tránh suspend nhầm
    // g_ChromeRootPids   = chrome.exe chính (main browser) — TUYỆT ĐỐI không Suspend
    // g_ChromeParentPids = chrome.exe có con chrome.exe (GPU/utility manager) — không Suspend
    // g_ChromeLeafPids   = chrome.exe không có con (renderer) — có thể EmptyWorkingSet
    static HashSet<uint> g_ChromeRootPids   = new HashSet<uint>();
    static HashSet<uint> g_ChromeParentPids = new HashSet<uint>();
    static HashSet<uint> g_ChromeLeafPids   = new HashSet<uint>();
    static DateTime      g_ChromeTreeUpdate = DateTime.MinValue;
    static uint   g_SelfPid  = 0;
    static int    g_CpuCores = 2;
    static double g_TotalRamGb = 4.0;

    static IntPtr g_ForegroundMask = (IntPtr)3;
    static IntPtr g_BackgroundMask = (IntPtr)2;
    static IntPtr g_SystemMask     = (IntPtr)2;

    // Tier 2: 45 minutes before RAM throttle, Tier 3: 60 minutes before suspend
    static int g_TimeRamThrottleSeconds = 2700;  // 45 minutes
    static int g_TimeSuspendSeconds     = 3600;  // 60 minutes
    static int g_TimeFrozenMinutes      = 20;    // Tier 4: 20 minutes in Tier 3

    // Track up to 3 most recent foreground apps
    static readonly Queue<uint> g_ForegroundApps = new Queue<uint>();
    static readonly object g_ForegroundLock = new object();

    static long     g_LastSystemIdle    = 0;
    static long     g_LastSystemKernel  = 0;
    static long     g_LastSystemUser    = 0;
    static DateTime g_LastSystemCpuTime = DateTime.MinValue;
    static double   g_SystemCpuLoad     = 0.0;

    // ===================== Fix 4: Tray Shared State =====================
    static volatile int    g_TrayFrozenCount          = 0;
    static volatile string g_TrayFgMode               = "TIET KIEM";
    static volatile bool   g_TrayIsPaused             = false;
    static volatile bool   g_PauseRequested           = false;
    static volatile bool   g_ExitRequested            = false;
    static volatile bool   g_ReloadWhitelistRequested = false;
    static volatile bool   g_ShowConsoleRequested     = false;

    static NotifyIcon  g_TrayIcon   = null;
    static Thread      g_TrayThread = null;
    static IntPtr      g_HwndConsole = IntPtr.Zero;

    // ===================== Fix 3: Protection Lists =====================
    static readonly HashSet<string> g_DoNotTouchList =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "idle", "system", "smss.exe", "csrss.exe", "wininit.exe",
        "lsass.exe", "lsaiso.exe", "winlogon.exe",
        "msmpeng.exe", "nissrv.exe", "mssense.exe",
        "sgrmbroker.exe"
    };

    static readonly HashSet<string> g_CriticalSystemList =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "idle", "system", "smss.exe", "csrss.exe", "wininit.exe",
        "services.exe", "lsass.exe", "winlogon.exe", "svchost.exe",
        "dwm.exe", "explorer.exe", "conhost.exe", "cmd.exe",
        "powershell.exe", "taskmgr.exe", "spoolsv.exe", "ctfmon.exe",
        "runtimebroker.exe", "audiodg.exe", "logonui.exe", "wmiprvse.exe",
        "fontdrvhost.exe", "registry", "memory compression",
        // Defender / Security
        "msmpeng.exe", "nissrv.exe", "mssense.exe",
        "securityhealthservice.exe", "securityhealthsystray.exe",
        "smartscreen.exe", "mpcmdrun.exe",
        // Windows Update
        "tiworker.exe", "trustedinstaller.exe", "mouscoreworker.exe",
        "wuauclt.exe", "usocoreworker.exe", "waasmedicagent.exe",
        "wudfhost.exe",
        // Shell / UWP
        "shellexperiencehost.exe", "startmenuexperiencehost.exe",
        "applicationframehost.exe", "sihost.exe", "textinputhost.exe",
        "searchhost.exe", "searchapp.exe", "searchindexer.exe",
        "systemsettings.exe",
        // COM / Task
        "dllhost.exe", "taskhostw.exe", "backgroundtaskhost.exe",
        "werfault.exe", "wermgr.exe",
        // Credential
        "lsaiso.exe", "sgrmbroker.exe", "lsm.exe"
    };

    static ConsoleCtrlDelegate g_CtrlHandler;

    // ===================== Fix 4: TRAY ICON =====================

    // Tạo icon hình tròn động với màu theo trạng thái CPU
    static Icon CreateDynamicIcon(double cpuLoad, bool paused) {
        var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp)) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Nền tối
            g.FillRectangle(new SolidBrush(Color.FromArgb(20, 20, 20)), 0, 0, 16, 16);

            // Màu theo CPU load
            Color barColor;
            if (paused)
                barColor = Color.FromArgb(120, 120, 120);
            else if (cpuLoad > 75)
                barColor = Color.FromArgb(220, 60, 60);
            else if (cpuLoad > 40)
                barColor = Color.FromArgb(220, 160, 30);
            else
                barColor = Color.FromArgb(50, 200, 100);

            // Vẽ thanh CPU
            int barH = Math.Max(1, (int)(12.0 * cpuLoad / 100.0));
            g.FillRectangle(new SolidBrush(Color.FromArgb(40, barColor)), 2, 2, 12, 12);
            g.FillRectangle(new SolidBrush(barColor), 2, 14 - barH, 12, barH);

            // Khung
            g.DrawRectangle(new Pen(Color.FromArgb(80, 80, 80)), 1, 1, 13, 13);

            if (paused) {
                g.FillRectangle(Brushes.White, 4, 4, 2, 8);
                g.FillRectangle(Brushes.White, 9, 4, 2, 8);
            }
        }
        IntPtr hIcon = bmp.GetHicon();
        bmp.Dispose();
        return Icon.FromHandle(hIcon);
    }

    static void TrayThreadProc() {
        Application.EnableVisualStyles();

        // Context menu
        var menu = new ContextMenuStrip();
        menu.Font = new Font("Segoe UI", 9f);

        // Header (disabled, hanya label)
        var lblHeader = new ToolStripLabel("  Neko Cpu Optimizer v1.0") {
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(50, 200, 100)
        };
        menu.Items.Add(lblHeader);
        menu.Items.Add(new ToolStripSeparator());

        var menuShow     = new ToolStripMenuItem("📺  Hiển thị cửa sổ");
        var menuPause    = new ToolStripMenuItem("⏸  Tạm dừng / Tiếp tục");
        var menuExit     = new ToolStripMenuItem("❌  Thoát");
        var menuSep      = new ToolStripSeparator();

        menuShow.Click   += (s, e) => { ShowConsole(); };
        menuPause.Click  += (s, e) => { g_PauseRequested        = true; };
        menuExit.Click   += (s, e) => { g_ExitRequested         = true; };

        menu.Items.Add(menuShow);
        menu.Items.Add(menuPause);
        menu.Items.Add(menuSep);
        menu.Items.Add(menuExit);

        // Tray icon
        g_TrayIcon = new NotifyIcon {
            Text    = "Neko Cpu Optimizer",
            Visible = true,
            ContextMenuStrip = menu
        };
        g_TrayIcon.Icon = CreateDynamicIcon(0, false);

        // Double-click → show console
        g_TrayIcon.DoubleClick += (s, e) => { ShowConsole(); };

        // Timer update icon + tooltip mỗi 2 giây
        var timer = new System.Windows.Forms.Timer { Interval = 2000 };
        timer.Tick += (s, e) => {
            bool paused    = g_TrayIsPaused;
            int  frozen    = g_TrayFrozenCount;
            double cpu     = g_SystemCpuLoad;
            string fgMode  = g_TrayFgMode;
            int    wlCount = g_UserWhitelist.Count;

            // Cập nhật icon màu động
            Icon oldIcon = g_TrayIcon.Icon;
            g_TrayIcon.Icon = CreateDynamicIcon(cpu, paused);
            try { if (oldIcon != null) oldIcon.Dispose(); } catch {}

            // Tooltip (max 63 ký tự)
            string status  = paused ? "⏸ TAM DUNG" : "▶ HOAT DONG";
            string tooltip = string.Format(
                "CPU: {0:F1}% | Dong bang: {1}\n{2}",
                cpu, frozen, status);
            if (tooltip.Length > 63) tooltip = tooltip.Substring(0, 63);
            g_TrayIcon.Text = tooltip;

            // Cập nhật label pause trong menu
            menuPause.Text = paused
                ? "▶  Tiếp tục tối ưu"
                : "⏸  Tạm dừng tối ưu";
        };
        timer.Start();

        Application.Run();

        timer.Stop();
        g_TrayIcon.Visible = false;
        g_TrayIcon.Dispose();
    }

    static void StartTrayThread() {
        g_TrayThread = new Thread(TrayThreadProc);
        g_TrayThread.IsBackground = true;
        g_TrayThread.SetApartmentState(ApartmentState.STA);
        g_TrayThread.Name = "TrayThread";
        g_TrayThread.Start();
        Thread.Sleep(500); // Đợi tray khởi tạo xong
    }

    static void StopTrayThread() {
        try {
            if (g_TrayIcon != null)
                g_TrayIcon.Visible = false;
            Application.Exit();
        } catch {}
    }

    static void ShowConsole() {
        if (g_HwndConsole != IntPtr.Zero) {
            ShowWindow(g_HwndConsole, SW_SHOW);
            ShowWindow(g_HwndConsole, SW_RESTORE);
            SetForegroundWindow(g_HwndConsole);
        }
    }

    static void HideConsoleAndNotify() {
        if (g_HwndConsole != IntPtr.Zero) {
            ShowWindow(g_HwndConsole, SW_HIDE);
        }
        // Balloon tip thông báo
        if (g_TrayIcon != null) {
            g_TrayIcon.ShowBalloonTip(
                4000,
                "Neko Cpu Optimizer đang chạy ngầm",
                string.Format(
                    "CPU: {0:F1}%  |  Đóng băng: {1} app\n" +
                    "Trạng thái: {2}\n" +
                    "Double-click vào icon để mở lại.",
                    g_SystemCpuLoad,
                    g_TrayFrozenCount,
                    g_TrayIsPaused ? "Đang tạm dừng" : "Đang tối ưu"),
                ToolTipIcon.Info);
        }
    }

    // ===================== Fix 1: WHITELIST =====================
    static void LoadWhitelist() {
        g_UserWhitelist.Clear();
        string path = "whitelist.txt";

        if (File.Exists(path)) {
            try {
                foreach (string line in File.ReadAllLines(path)) {
                    string cleanLine = line.Trim();
                    if (string.IsNullOrEmpty(cleanLine) || cleanLine.StartsWith("#")) continue;

                    string[] parts    = cleanLine.Split(':');
                    string appName    = parts[0].Trim();
                    AppType appType   = AppType.Normal;
                    int wakeInterval  = 30;

                    if (parts.Length >= 2) {
                        switch (parts[1].Trim().ToLowerInvariant()) {
                            case "browser":   appType = AppType.Browser;   break;
                            case "audio":     appType = AppType.Audio;     break;
                            case "messenger": appType = AppType.Messenger; break;
                            default:          appType = AppType.Normal;    break;
                        }
                    }
                    if (parts.Length >= 3) {
                        int parsed;
                        if (int.TryParse(parts[2].Trim(), out parsed) && parsed >= 5)
                            wakeInterval = parsed;
                    }

                    if (!string.IsNullOrEmpty(appName)) {
                        g_UserWhitelist[appName] = new WhitelistEntry {
                            Name                = appName,
                            Type                = appType,
                            WakeIntervalSeconds = wakeInterval
                        };
                    }
                }
            } catch {}
        } else {
            try {
                File.WriteAllLines(path, new string[] {
                    "# Danh sach app duoc phep chay nen",
                    "# Format: app.exe:type  hoac  app.exe (mac dinh: normal)",
                    "# Types: browser | audio | messenger | normal",
                    "# Messenger: app.exe:messenger:15  (wake moi 15 giay)",
                    "chrome.exe:browser",
                    "discord.exe:messenger",
                    "spotify.exe:audio",
                    "zalo.exe:messenger"
                });
            } catch {}
            g_UserWhitelist["chrome.exe"]  = new WhitelistEntry { Name="chrome.exe",  Type=AppType.Browser,   WakeIntervalSeconds=30 };
            g_UserWhitelist["discord.exe"] = new WhitelistEntry { Name="discord.exe", Type=AppType.Messenger, WakeIntervalSeconds=30 };
            g_UserWhitelist["spotify.exe"] = new WhitelistEntry { Name="spotify.exe", Type=AppType.Audio,     WakeIntervalSeconds=30 };
            g_UserWhitelist["zalo.exe"]    = new WhitelistEntry { Name="zalo.exe",    Type=AppType.Messenger, WakeIntervalSeconds=30 };
        }
    }

    static bool InitializeSystem() {
        g_SelfPid  = (uint)Process.GetCurrentProcess().Id;
        g_CpuCores = Environment.ProcessorCount;

        if (g_CpuCores >= 4) {
            int t1Cores = (int)Math.Ceiling(g_CpuCores * 0.75);
            int t3Cores = Math.Max(1, (int)Math.Ceiling(g_CpuCores * 0.25));
            g_ForegroundMask = (IntPtr)((1UL << t1Cores) - 1);
            g_BackgroundMask = (IntPtr)6;
            g_SystemMask     = (IntPtr)(((1UL << t3Cores) - 1) << (g_CpuCores - t3Cores));
        } else if (g_CpuCores == 3) {
            g_ForegroundMask = (IntPtr)3; g_BackgroundMask = (IntPtr)6; g_SystemMask = (IntPtr)4;
        } else if (g_CpuCores == 2) {
            g_ForegroundMask = (IntPtr)3; g_BackgroundMask = (IntPtr)2; g_SystemMask = (IntPtr)2;
        } else {
            g_ForegroundMask = (IntPtr)1; g_BackgroundMask = (IntPtr)1; g_SystemMask = (IntPtr)1;
        }

        MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();
        memStatus.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        if (GlobalMemoryStatusEx(ref memStatus))
            g_TotalRamGb = memStatus.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);

        // Fixed timeouts: 45 min tier 2, 60 min tier 3 (regardless of RAM)
        // RAM-aware throttling will be applied at runtime based on available memory

        LoadWhitelist();
        return true;
    }

    static string GetProcessPath(uint pid) {
        IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess != IntPtr.Zero) {
            StringBuilder sb = new StringBuilder(1024);
            int size = sb.Capacity;
            if (QueryFullProcessImageName(hProcess, 0, sb, ref size)) {
                CloseHandle(hProcess);
                return sb.ToString();
            }
            CloseHandle(hProcess);
        }
        return "";
    }

    // ===================== Fix 3: Protection Guards =====================
    static bool IsDoNotTouch(uint pid, string name) {
        if (pid == 0 || pid == 4) return true;
        return g_DoNotTouchList.Contains(name);
    }

    static bool IsCriticalSystemProcess(uint pid, string name, string fullPath) {
        if (pid == 0 || pid == 4 || pid == g_SelfPid) return true;
        if (g_CriticalSystemList.Contains(name)) return true;
        if (!string.IsNullOrEmpty(fullPath)) {
            string lower = fullPath.ToLowerInvariant().Replace('/', '\\');
            if (lower.StartsWith(@"c:\windows\") ||
                lower.StartsWith(@"c:\program files\windowsapps\"))
                return true;
        }
        return false;
    }

    static void ResumeAndResetAll() {
        foreach (var pair in g_ProcessStates) {
            ProcessState state = pair.Value;
            if (state.IsSuspended) {
                IntPtr hProcess = OpenProcess(PROCESS_SUSPEND_RESUME, false, state.Pid);
                if (hProcess != IntPtr.Zero) { NtResumeProcess(hProcess); CloseHandle(hProcess); }
                state.IsSuspended = false;
            }
            IntPtr hProc = OpenProcess(PROCESS_SET_INFORMATION, false, state.Pid);
            if (hProc != IntPtr.Zero) {
                SetPriorityClass(hProc, NORMAL_PRIORITY_CLASS);
                SetProcessAffinityMask(hProc, g_ForegroundMask);
                CloseHandle(hProc);
            }
        }
        g_ProcessStates.Clear();
    }

    // ===================== Fix 2: Task Scheduler Startup =====================
    static void RunProcess(string fileName, string arguments) {
        try {
            var psi = new ProcessStartInfo {
                FileName = fileName, Arguments = arguments,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (Process p = Process.Start(psi)) { p.WaitForExit(5000); }
        } catch {}
    }

    static bool RegisterStartup(bool enable) {
        try {
            string exePath = Process.GetCurrentProcess().MainModule.FileName;
            if (enable) {
                RunProcess("schtasks.exe", string.Format(
                    "/Create /TN \"{0}\" /TR \"\\\"{1}\\\" --startup\" /SC ONLOGON /RL HIGHEST /F",
                    TASK_NAME, exePath));
            } else {
                RunProcess("schtasks.exe", string.Format("/Delete /TN \"{0}\" /F", TASK_NAME));
            }
            return true;
        } catch { return false; }
    }

    static bool IsStartupEnabled() {
        try {
            var psi = new ProcessStartInfo {
                FileName = "schtasks.exe",
                Arguments = string.Format("/Query /TN \"{0}\"", TASK_NAME),
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (Process p = Process.Start(psi)) {
                p.WaitForExit(3000);
                return p.ExitCode == 0;
            }
        } catch { return false; }
    }

    static void MigrateFromRegistryStartup() {
        try {
            RegistryKey key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key != null) {
                if (key.GetValue("Neko_Cpu_Optimizer") != null)
                    key.DeleteValue("Neko_Cpu_Optimizer", false);
                key.Close();
            }
        } catch {}
    }

    // ===================== CPU Load =====================
    static double CalculateSystemCpuLoad(DateTime now) {
        long idle, kernel, user;
        if (GetSystemTimes(out idle, out kernel, out user)) {
            if (g_LastSystemCpuTime != DateTime.MinValue) {
                long idleDiff  = idle   - g_LastSystemIdle;
                long kDiff     = kernel - g_LastSystemKernel;
                long uDiff     = user   - g_LastSystemUser;
                long total     = kDiff  + uDiff;
                if (total > 0)
                    g_SystemCpuLoad = Math.Max(0, Math.Min(100,
                        100.0 * (total - idleDiff) / total));
            }
            g_LastSystemIdle = idle; g_LastSystemKernel = kernel;
            g_LastSystemUser = user; g_LastSystemCpuTime = now;
        }
        return g_SystemCpuLoad;
    }

    // Check available RAM percentage (0-100)
    static double GetAvailableRamPercent() {
        MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();
        memStatus.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        if (GlobalMemoryStatusEx(ref memStatus))
            return (memStatus.ullAvailPhys * 100.0) / memStatus.ullTotalPhys;
        return 50.0;
    }

    static void UpdateConsoleTitle(int frozenCount, bool isPaused, double cpu, string fgMode) {
        SetConsoleTitle(string.Format(
            "[Neko Cpu Optimizer v1.0] CPU:{0:F1}% | {1} | Dong bang:{2} | {3}",
            cpu, fgMode, frozenCount, isPaused ? "TAM DUNG" : "HOAT DONG"));
    }

    // ===================== Fix 1: Whitelist Handlers =====================
    static void HandleWhitelistedProcess(ProcessState state, WhitelistEntry entry,
                                          uint pid, DateTime now) {
        switch (entry.Type) {
            case AppType.Audio:     HandleAudioProcess(state, pid);                 break;
            case AppType.Messenger: HandleMessengerProcess(state, entry, pid, now); break;
            case AppType.Browser:   HandleBrowserProcess(state, pid, now);          break;
            default:                HandleNormalWhitelistProcess(state, pid);       break;
        }
        state.Tier = PriorityTier.TIER_2_NORMAL;
    }

    static void HandleAudioProcess(ProcessState state, uint pid) {
        if (state.IsSuspended) {
            IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
            if (h != IntPtr.Zero) { NtResumeProcess(h); CloseHandle(h); }
            state.IsSuspended = false;
        }
        IntPtr hProc = OpenProcess(PROCESS_SET_INFORMATION, false, pid);
        if (hProc != IntPtr.Zero) {
            SetPriorityClass(hProc, BELOW_NORMAL_PRIORITY_CLASS);
            SetProcessAffinityMask(hProc, g_BackgroundMask);
            CloseHandle(hProc);
        }
    }

    static void HandleMessengerProcess(ProcessState state, WhitelistEntry entry,
                                        uint pid, DateTime now) {
        IntPtr hCheck = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hCheck != IntPtr.Zero) {
            long cT, eT, kT, uT;
            if (GetProcessTimes(hCheck, out cT, out eT, out kT, out uT)) {
                double elapsed = (now - state.LastCpuCheckTime).TotalSeconds;
                if (elapsed > 0) {
                    long ticks = (kT - state.LastKernelTime) + (uT - state.LastUserTime);
                    state.CpuUsagePercent = (ticks / 10000000.0 / elapsed) * 100.0 / g_CpuCores;
                }
                state.LastKernelTime = kT; state.LastUserTime = uT;
                state.LastCpuCheckTime = now;
            }
            CloseHandle(hCheck);
        }

        bool timeToWake = state.IsSuspended &&
            (now - state.LastWakeTime).TotalSeconds >= entry.WakeIntervalSeconds;

        if (timeToWake) {
            IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME | PROCESS_SET_INFORMATION, false, pid);
            if (h != IntPtr.Zero) {
                NtResumeProcess(h);
                SetPriorityClass(h, BELOW_NORMAL_PRIORITY_CLASS);
                SetProcessAffinityMask(h, g_BackgroundMask);
                CloseHandle(h);
            }
            state.IsSuspended = false; state.LastWakeTime = now; state.IdleCycles = 0;
        } else if (!state.IsSuspended) {
            if (state.CpuUsagePercent < 0.2) {
                state.IdleCycles++;
                if (state.IdleCycles >= 3) {
                    IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME | PROCESS_SET_QUOTA, false, pid);
                    if (h != IntPtr.Zero) { NtSuspendProcess(h); EmptyWorkingSet(h); CloseHandle(h); }
                    state.IsSuspended = true; state.LastWakeTime = now;
                }
            } else {
                state.IdleCycles = 0;
                IntPtr h = OpenProcess(PROCESS_SET_INFORMATION, false, pid);
                if (h != IntPtr.Zero) {
                    SetPriorityClass(h, BELOW_NORMAL_PRIORITY_CLASS);
                    SetProcessAffinityMask(h, g_BackgroundMask);
                    CloseHandle(h);
                }
            }
        }
    }

    // ===================== Chrome Process Tree Analysis =====================
    // Chrome kiến trúc đa tiến trình:
    //   Root process (main browser): cha KHÔNG phải chrome.exe → không bao giờ Suspend
    //   Parent processes (GPU/utility manager): có con là chrome.exe → không Suspend
    //   Leaf processes (renderer tabs): không có con chrome.exe → có thể EmptyWorkingSet
    // → TUYỆT ĐỐI không Suspend bất kỳ chrome.exe nào (GPU crash = màn hình đen)
    static void RebuildChromeTree(List<uint> chromePids) {
        g_ChromeRootPids.Clear();
        g_ChromeParentPids.Clear();
        g_ChromeLeafPids.Clear();

        if (chromePids.Count == 0) return;

        // Đọc PPID của mọi process trong hệ thống
        var ppidMap = new Dictionary<uint, uint>();
        IntPtr hSnap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (hSnap != (IntPtr)(-1) && hSnap != IntPtr.Zero) {
            PROCESSENTRY32 pe = new PROCESSENTRY32();
            pe.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
            if (Process32First(hSnap, ref pe)) {
                do { ppidMap[pe.th32ProcessID] = pe.th32ParentProcessID; }
                while (Process32Next(hSnap, ref pe));
            }
            CloseHandle(hSnap);
        }

        var chromeSet = new HashSet<uint>(chromePids);

        // Xác định chrome processes nào là cha của chrome processes khác
        foreach (uint pid in chromePids) {
            uint ppid;
            if (ppidMap.TryGetValue(pid, out ppid) && chromeSet.Contains(ppid))
                g_ChromeParentPids.Add(ppid); // ppid là process cha
        }

        // Phân loại từng chrome process
        foreach (uint pid in chromePids) {
            uint ppid;
            bool hasChromParent = ppidMap.TryGetValue(pid, out ppid) && chromeSet.Contains(ppid);

            if (!hasChromParent) {
                g_ChromeRootPids.Add(pid);      // Không có cha chrome = main browser
            } else if (g_ChromeParentPids.Contains(pid)) {
                // Vừa có cha chrome vừa có con chrome = GPU process manager
                // → đã có trong g_ChromeParentPids
            } else {
                g_ChromeLeafPids.Add(pid);      // Có cha chrome nhưng không có con = renderer
            }
        }
    }

    static void HandleBrowserProcess(ProcessState state, uint pid, DateTime now) {
        // === Chrome Safety Rules ===
        // 1. KHÔNG BAO GIỜ Suspend bất kỳ chrome.exe nào — GPU process crash = màn hình đen
        // 2. Root process (main browser) + GPU parent: chỉ BELOW_NORMAL, không EmptyWorkingSet
        // 3. Renderer leaf: BELOW_NORMAL + EmptyWorkingSet nếu CPU thấp liên tiếp
        // 4. Resume ngay nếu bị Suspend từ trước (cleanup state cũ)

        // Đảm bảo không bị Suspend (cleanup state cũ nếu có)
        if (state.IsSuspended) {
            IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
            if (h != IntPtr.Zero) { NtResumeProcess(h); CloseHandle(h); }
            state.IsSuspended = false;
        }

        // Đo CPU để biết mức hoạt động
        IntPtr hCheck = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hCheck != IntPtr.Zero) {
            long cT, eT, kT, uT;
            if (GetProcessTimes(hCheck, out cT, out eT, out kT, out uT)) {
                double elapsed = (now - state.LastCpuCheckTime).TotalSeconds;
                if (elapsed > 0) {
                    long ticks = (kT - state.LastKernelTime) + (uT - state.LastUserTime);
                    state.CpuUsagePercent = (ticks / 10000000.0 / elapsed) * 100.0 / g_CpuCores;
                }
                state.LastKernelTime = kT; state.LastUserTime = uT; state.LastCpuCheckTime = now;
            }
            CloseHandle(hCheck);
        }

        // Root process / GPU manager: không động vào bộ nhớ
        bool isRoot   = g_ChromeRootPids.Contains(pid);
        bool isParent = g_ChromeParentPids.Contains(pid);
        bool isLeaf   = g_ChromeLeafPids.Contains(pid);

        if (isRoot || isParent) {
            // Main browser + GPU process: chỉ giảm priority nhẹ, KHÔNG EmptyWorkingSet
            IntPtr h = OpenProcess(PROCESS_SET_INFORMATION, false, pid);
            if (h != IntPtr.Zero) {
                SetPriorityClass(h, BELOW_NORMAL_PRIORITY_CLASS);
                SetProcessAffinityMask(h, g_BackgroundMask);
                CloseHandle(h);
            }
            state.IdleCycles = 0;
        } else {
            // Renderer tab (leaf): có thể EmptyWorkingSet khi idle dài
            // Nhưng vẫn KHÔNG Suspend
            if (state.CpuUsagePercent < 0.2) {
                state.IdleCycles++;
                if (state.IdleCycles >= 5) { // Cần 5 chu kỳ (10 giây) mới EmptyWorkingSet
                    IntPtr h = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_SET_QUOTA, false, pid);
                    if (h != IntPtr.Zero) {
                        SetPriorityClass(h, IDLE_PRIORITY_CLASS);
                        SetProcessAffinityMask(h, g_BackgroundMask);
                        EmptyWorkingSet(h); CloseHandle(h);
                    }
                } else {
                    IntPtr h = OpenProcess(PROCESS_SET_INFORMATION, false, pid);
                    if (h != IntPtr.Zero) {
                        SetPriorityClass(h, BELOW_NORMAL_PRIORITY_CLASS);
                        SetProcessAffinityMask(h, g_BackgroundMask);
                        CloseHandle(h);
                    }
                }
            } else {
                state.IdleCycles = 0;
                IntPtr h = OpenProcess(PROCESS_SET_INFORMATION, false, pid);
                if (h != IntPtr.Zero) {
                    SetPriorityClass(h, BELOW_NORMAL_PRIORITY_CLASS);
                    SetProcessAffinityMask(h, g_BackgroundMask);
                    CloseHandle(h);
                }
            }
        }
    }

    static void HandleNormalWhitelistProcess(ProcessState state, uint pid) {
        IntPtr h = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_SET_QUOTA, false, pid);
        if (h != IntPtr.Zero) {
            SetPriorityClass(h, BELOW_NORMAL_PRIORITY_CLASS);
            SetProcessAffinityMask(h, g_BackgroundMask);
            EmptyWorkingSet(h); CloseHandle(h);
        }
    }

    // ===================== Main Optimization Loop =====================
    static void ProcessOptimizationStep(ref string foregroundMode) {
        DateTime now = DateTime.UtcNow;
        double sysCpu = CalculateSystemCpuLoad(now);
        double ramAvailPercent = GetAvailableRamPercent();

        // Apply CPU-based modulation to timeouts
        double mod = 1.0;
        if (sysCpu > 75) mod = 0.8; else if (sysCpu < 30) mod = 1.2;

        int activeRamLimit     = (int)(g_TimeRamThrottleSeconds * mod);
        int activeSuspendLimit = Math.Max(3600, (int)(g_TimeSuspendSeconds * mod));

        // Get current foreground window
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;
        uint foregroundPid = 0;
        GetWindowThreadProcessId(hwnd, out foregroundPid);
        if (foregroundPid == 0) return;

        // Track up to 3 most recent foreground apps
        lock (g_ForegroundLock) {
            if (!g_ForegroundApps.Contains(foregroundPid)) {
                g_ForegroundApps.Enqueue(foregroundPid);
                if (g_ForegroundApps.Count > 3)
                    g_ForegroundApps.Dequeue();
            }
        }

        HashSet<uint> activePids = new HashSet<uint>();
        Process[] processes = Process.GetProcesses();
        int frozenCount = 0;

        // Build Chrome process tree
        var chromePidList = new List<uint>();
        foreach (Process p in processes) {
            if (p.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase))
                chromePidList.Add((uint)p.Id);
        }
        RebuildChromeTree(chromePidList);

        foreach (Process p in processes) {
            uint pid = (uint)p.Id;
            activePids.Add(pid);
            if (pid == g_SelfPid) continue;

            string name     = p.ProcessName + ".exe";
            string fullPath = GetProcessPath(pid);

            if (IsDoNotTouch(pid, name)) continue;

            bool isCriticalSystem = IsCriticalSystemProcess(pid, name, fullPath);
            bool isWhitelisted    = g_UserWhitelist.ContainsKey(name);
            WhitelistEntry wEntry = isWhitelisted ? g_UserWhitelist[name] : null;

            // Check if process is in recent foreground list
            bool isRecentForeground = false;
            lock (g_ForegroundLock) {
                isRecentForeground = g_ForegroundApps.Contains(pid);
            }

            bool isLaunching = false;
            try {
                double age = (now - p.StartTime.ToUniversalTime()).TotalSeconds;
                if (age >= 0 && age < 15) isLaunching = true;
            } catch {}

            if (!g_ProcessStates.ContainsKey(pid)) {
                g_ProcessStates[pid] = new ProcessState {
                    Pid = pid, Name = name, IsSuspended = false,
                    IsRamThrottled = false, IsTemporarilyPromoted = false,
                    Tier = (isRecentForeground || pid == foregroundPid) ? PriorityTier.TIER_1_REALTIME : PriorityTier.TIER_2_NORMAL,
                    LastStateChangeTime = now, EnteredBackgroundTime = now,
                    EnteredSuspendedTime = now, LastCpuCheckTime = now, LastWakeTime = now
                };
            }

            ProcessState state = g_ProcessStates[pid];
            if (state.IsSuspended) frozenCount++;

            if (pid == foregroundPid || isRecentForeground) {
                // TIER 1: FOREGROUND (current or recent in last 3)
                IntPtr hCheck = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (hCheck != IntPtr.Zero) {
                    long cT, eT, kT, uT;
                    if (GetProcessTimes(hCheck, out cT, out eT, out kT, out uT)) {
                        double elapsed = (now - state.LastCpuCheckTime).TotalSeconds;
                        if (elapsed > 0) {
                            long ticks = (kT - state.LastKernelTime) + (uT - state.LastUserTime);
                            state.CpuUsagePercent = (ticks / 10000000.0 / elapsed) * 100.0 / g_CpuCores;
                        }
                        state.LastKernelTime = kT; state.LastUserTime = uT; state.LastCpuCheckTime = now;
                        uint pri = HIGH_PRIORITY_CLASS;
                        if (state.CpuUsagePercent > 10) { state.LowLoadCycles = 0; foregroundMode = "TOI DA"; }
                        else if (state.CpuUsagePercent <= 5) {
                            state.LowLoadCycles++;
                            if (state.LowLoadCycles >= 3) { foregroundMode = "TIET KIEM"; pri = NORMAL_PRIORITY_CLASS; }
                        }
                        IntPtr hSet = OpenProcess(PROCESS_SET_INFORMATION, false, pid);
                        if (hSet != IntPtr.Zero) { SetPriorityClass(hSet, pri); SetProcessAffinityMask(hSet, g_ForegroundMask); CloseHandle(hSet); }
                    }
                    CloseHandle(hCheck);
                }
                if (state.Tier != PriorityTier.TIER_1_REALTIME) {
                    if (state.IsSuspended) {
                        IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
                        if (h != IntPtr.Zero) { NtResumeProcess(h); CloseHandle(h); }
                        state.IsSuspended = false;
                    }
                    IntPtr hSet = OpenProcess(PROCESS_SET_INFORMATION, false, pid);
                    if (hSet != IntPtr.Zero) { SetPriorityClass(hSet, HIGH_PRIORITY_CLASS); SetProcessAffinityMask(hSet, g_ForegroundMask); CloseHandle(hSet); }
                    state.Tier = PriorityTier.TIER_1_REALTIME; state.IsTemporarilyPromoted = false;
                    state.LastStateChangeTime = now; state.IdleCycles = 0; state.LowLoadCycles = 0;
                }
            }
            else if (isLaunching) {
                if (state.IsSuspended) {
                    IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
                    if (h != IntPtr.Zero) { NtResumeProcess(h); CloseHandle(h); }
                    state.IsSuspended = false;
                }
                IntPtr hSet = OpenProcess(PROCESS_SET_INFORMATION, false, pid);
                if (hSet != IntPtr.Zero) { SetPriorityClass(hSet, NORMAL_PRIORITY_CLASS); SetProcessAffinityMask(hSet, g_ForegroundMask); CloseHandle(hSet); }
                state.Tier = PriorityTier.TIER_2_NORMAL; state.IsTemporarilyPromoted = false;
                state.IsRamThrottled = false; state.EnteredBackgroundTime = now; state.IdleCycles = 0;
            }
            else if (isCriticalSystem) {
                // Fix 3: Light throttle only
                if (state.Tier == PriorityTier.TIER_1_REALTIME) {
                    state.Tier = PriorityTier.TIER_3_LOW; state.EnteredBackgroundTime = now; state.LastStateChangeTime = now;
                }
                bool isSvchost = name.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase);
                IntPtr hProc = OpenProcess(PROCESS_SET_INFORMATION, false, pid);
                if (hProc != IntPtr.Zero) {
                    SetPriorityClass(hProc, IDLE_PRIORITY_CLASS);
                    if (!isSvchost) SetProcessAffinityMask(hProc, g_SystemMask);
                    CloseHandle(hProc);
                }
                state.Tier = PriorityTier.TIER_3_LOW;
            }
            else if (isWhitelisted) {
                if (state.Tier == PriorityTier.TIER_1_REALTIME) {
                    state.Tier = PriorityTier.TIER_2_NORMAL; state.EnteredBackgroundTime = now;
                    state.IsRamThrottled = false; state.IdleCycles = 0;
                }
                HandleWhitelistedProcess(state, wEntry, pid, now);
            }
            else {
                // USER NORMAL APPS - MLFQ full
                if (state.Tier == PriorityTier.TIER_1_REALTIME) {
                    state.IsTemporarilyPromoted = false; state.LastStateChangeTime = now;
                    state.EnteredBackgroundTime = now; state.IsRamThrottled = false;
                    state.Tier = PriorityTier.TIER_2_NORMAL;
                }

                double bgSec = (now - state.EnteredBackgroundTime).TotalSeconds;

                if (state.Tier == PriorityTier.TIER_2_NORMAL) {
                    IntPtr hProc = OpenProcess(PROCESS_SET_INFORMATION, false, pid);
                    if (hProc != IntPtr.Zero) { SetPriorityClass(hProc, BELOW_NORMAL_PRIORITY_CLASS); SetProcessAffinityMask(hProc, g_BackgroundMask); CloseHandle(hProc); }

                    if (state.IsTemporarilyPromoted) {
                        if ((now - state.PromotionStartTime).TotalSeconds >= 3) {
                            // Only suspend if RAM is truly low (< 15%)
                            if (ramAvailPercent < 15.0) {
                                IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME | PROCESS_SET_QUOTA, false, pid);
                                if (h != IntPtr.Zero) { NtSuspendProcess(h); state.IsSuspended = true; EmptyWorkingSet(h); CloseHandle(h); }
                                state.Tier = PriorityTier.TIER_3_LOW; state.IsTemporarilyPromoted = false;
                                state.LastStateChangeTime = now; state.EnteredSuspendedTime = now;
                            }
                        }
                    } else if (bgSec >= activeSuspendLimit) {
                        // Only suspend if RAM is truly low (< 15%) OR if it's been very long (2+ hours)
                        if (ramAvailPercent < 15.0 || bgSec >= 7200) {
                            IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME | PROCESS_SET_QUOTA, false, pid);
                            if (h != IntPtr.Zero) { NtSuspendProcess(h); state.IsSuspended = true; EmptyWorkingSet(h); CloseHandle(h); }
                            state.Tier = PriorityTier.TIER_3_LOW; state.LastStateChangeTime = now; state.EnteredSuspendedTime = now;
                        }
                    } else if (bgSec >= activeRamLimit && ramAvailPercent > 20.0) {
                        // Only RAM throttle if RAM is still abundant (> 20%)
                        IntPtr h = OpenProcess(PROCESS_SET_QUOTA, false, pid);
                        if (h != IntPtr.Zero) { EmptyWorkingSet(h); CloseHandle(h); }
                        state.IsRamThrottled = true;
                    }
                }
                else if (state.Tier == PriorityTier.TIER_3_LOW) {
                    double suspMin = (now - state.EnteredSuspendedTime).TotalMinutes;
                    // Only freeze to tier 4 if RAM is truly low
                    if (suspMin >= g_TimeFrozenMinutes && ramAvailPercent < 15.0) {
                        state.Tier = PriorityTier.TIER_4_FROZEN; state.IsTemporarilyPromoted = false;
                        if (!state.IsSuspended) {
                            IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME | PROCESS_SET_QUOTA, false, pid);
                            if (h != IntPtr.Zero) { NtSuspendProcess(h); state.IsSuspended = true; EmptyWorkingSet(h); CloseHandle(h); }
                        }
                        Console.WriteLine("\n[Tang 4] {0} (PID:{1}) dong bang hoan toan.", state.Name, state.Pid);
                        Console.Write("optimizer> ");
                    } else {
                        if ((now - state.LastStateChangeTime).TotalSeconds >= 45) {
                            IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME | PROCESS_SET_INFORMATION, false, pid);
                            if (h != IntPtr.Zero) { NtResumeProcess(h); state.IsSuspended = false; SetPriorityClass(h, IDLE_PRIORITY_CLASS); SetProcessAffinityMask(h, g_BackgroundMask); CloseHandle(h); }
                            state.Tier = PriorityTier.TIER_2_NORMAL; state.IsTemporarilyPromoted = true;
                            state.PromotionStartTime = now; state.LastStateChangeTime = now;
                        }
                    }
                }
                else if (state.Tier == PriorityTier.TIER_4_FROZEN) {
                    if (!state.IsSuspended) {
                        IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME | PROCESS_SET_QUOTA, false, pid);
                        if (h != IntPtr.Zero) { NtSuspendProcess(h); state.IsSuspended = true; EmptyWorkingSet(h); CloseHandle(h); }
                    }
                }
            }
        }

        // Cập nhật shared state cho tray
        g_TrayFrozenCount = frozenCount;
        g_TrayFgMode      = foregroundMode;

        // Cleanup dead processes
        List<uint> toRemove = new List<uint>();
        foreach (uint k in g_ProcessStates.Keys)
            if (!activePids.Contains(k)) toRemove.Add(k);
        foreach (uint k in toRemove) g_ProcessStates.Remove(k);
    }

    static bool ConsoleCtrlCheck(int ctrlType) {
        ResumeAndResetAll();
        StopTrayThread();
        return true;
    }

    static int CountByType(AppType type) {
        int n = 0;
        foreach (var e in g_UserWhitelist.Values) if (e.Type == type) n++;
        return n;
    }

    // ===================== ENTRY POINT =====================
    static void Main(string[] args) {
        bool isNewInstance;
        using (Mutex m_Mutex = new Mutex(true, "Global\\Neko_Cpu_Optimizer", out isNewInstance)) {
            if (!isNewInstance) {
                // Toggle show/hide instance đang chạy
                try {
                    RegistryKey regKey = Registry.CurrentUser.OpenSubKey(@"Software\Neko_Cpu_Optimizer", false);
                    if (regKey != null) {
                        object val = regKey.GetValue("ConsoleHWND");
                        regKey.Close();
                        if (val != null) {
                            IntPtr prevHwnd = (IntPtr)Convert.ToInt64(val);
                            if (prevHwnd != IntPtr.Zero) {
                                if (IsWindowVisible(prevHwnd)) ShowWindow(prevHwnd, SW_HIDE);
                                else { ShowWindow(prevHwnd, SW_SHOW); ShowWindow(prevHwnd, SW_RESTORE); SetForegroundWindow(prevHwnd); }
                                Thread.Sleep(1000); return;
                            }
                        }
                    }
                } catch {}
                Console.WriteLine("[i] Neko Cpu Optimizer hien dang chay an trong he thong.");
                Thread.Sleep(2000); return;
            }

            if (!IsUserAnAdmin()) {
                string szPath = Process.GetCurrentProcess().MainModule.FileName;
                var psi = new ProcessStartInfo { FileName = szPath, Verb = "runas", UseShellExecute = true };
                if (args.Length > 0) psi.Arguments = string.Join(" ", args);
                try { Process.Start(psi); }
                catch { Console.WriteLine("[-] LOI: Can quyen Administrator."); Console.ReadKey(); }
                return;
            }

            bool isStartupMode = false;
            foreach (string arg in args) if (arg == "--startup") { isStartupMode = true; break; }

            // For WinExe, allocate console if needed
            if (GetConsoleWindow() == IntPtr.Zero) {
                AllocConsole();
            }
            g_HwndConsole = GetConsoleWindow();
            if (isStartupMode && g_HwndConsole != IntPtr.Zero)
                ShowWindow(g_HwndConsole, SW_HIDE);

            if (g_HwndConsole != IntPtr.Zero) {
                try {
                    RegistryKey regKey = Registry.CurrentUser.CreateSubKey(@"Software\Neko_Cpu_Optimizer");
                    if (regKey != null) { regKey.SetValue("ConsoleHWND", g_HwndConsole.ToInt64(), RegistryValueKind.QWord); regKey.Close(); }
                } catch {}
            }

            if (!InitializeSystem()) {
                Console.WriteLine("[-] LOI: Khong the khoi tao."); Console.ReadKey(); return;
            }

            MigrateFromRegistryStartup();
            if (!IsStartupEnabled()) RegisterStartup(true);

            g_CtrlHandler = new ConsoleCtrlDelegate(ConsoleCtrlCheck);
            SetConsoleCtrlHandler(g_CtrlHandler, true);

            // Fix 4: Khởi động Tray Icon
            StartTrayThread();

            Console.WriteLine("==================================================");
            Console.WriteLine("  hello user!                                     ");
            Console.WriteLine("==================================================");
            Console.WriteLine("==================================================");
            Console.WriteLine("[i] short cut:");
            Console.WriteLine("    [SPACE] stop / continue");
            Console.WriteLine("    [H]     hide in taskbar");
            Console.WriteLine("    [R]     reset whitelist.txt");
            Console.WriteLine("    [ESC]   reset all and exit");
            Console.WriteLine("==================================================");

            bool   isPaused      = false;
            bool   running       = true;
            string foregroundMode = "TIET KIEM";

            while (running) {
                // Kiểm tra yêu cầu từ tray menu
                if (g_ExitRequested)   { running = false; break; }
                if (g_PauseRequested)  {
                    g_PauseRequested = false;
                    isPaused = !isPaused;
                    g_TrayIsPaused = isPaused;
                    if (isPaused) { ResumeAndResetAll(); Console.Write("\r[TAM DUNG] Nhan SPACE de tiep tuc...         "); }
                    else          { Console.Write("\r[HOAT DONG]                                  "); }
                }
                if (g_ReloadWhitelistRequested) {
                    g_ReloadWhitelistRequested = false;
                    LoadWhitelist();
                    // Removed whitelist reload display as per user request
                }
                if (g_ShowConsoleRequested) {
                    g_ShowConsoleRequested = false;
                    ShowConsole();
                }

                // Keyboard input
                for (int i = 0; i < 10 && running && !g_ExitRequested; i++) {
                    if (_kbhit() != 0) {
                        int ch = _getch();
                        switch (ch) {
                            case 27: running = false; break;
                            case ' ':
                                isPaused = !isPaused; g_TrayIsPaused = isPaused;
                                if (isPaused) { ResumeAndResetAll(); Console.Write("\r[TAM DUNG] Nhan SPACE de tiep tuc...         "); }
                                else          { Console.Write("\r[HOAT DONG]                                  "); }
                                break;
                            case 'h': case 'H':
                                HideConsoleAndNotify();
                                break;
                            case 'r': case 'R':
                                LoadWhitelist();
                                // Removed whitelist reload display as per user request
                                break;
                        }
                    }
                    Thread.Sleep(200);
                }

                if (!running || g_ExitRequested) break;

                if (!isPaused) {
                    try { ProcessOptimizationStep(ref foregroundMode); } catch {}
                }

                UpdateConsoleTitle(g_TrayFrozenCount, isPaused, g_SystemCpuLoad,
                    isPaused ? "TAM DUNG" : foregroundMode);
            }

            ResumeAndResetAll();
            StopTrayThread();

            try {
                RegistryKey regKey = Registry.CurrentUser.CreateSubKey(@"Software\CPU_RAM_MLFQ_Optimizer");
                if (regKey != null) { regKey.DeleteValue("ConsoleHWND", false); regKey.Close(); }
            } catch {}

            Console.WriteLine("\n[+] Da khoi phuc he thong va thoat!");
        }
    }
}
