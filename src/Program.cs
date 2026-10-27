using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BatteryCare
{
    // ---------------------------------------------------------------- paths / log

    static class Paths
    {
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatteryCare");
        public static readonly string ConfigFile = Path.Combine(Dir, "config.ini");
        public static readonly string LogDir = Path.Combine(Dir, "logs");

        public static void EnsureDir()
        {
            try { Directory.CreateDirectory(Dir); Directory.CreateDirectory(LogDir); } catch { }
        }
    }

    static class Log
    {
        static readonly object Gate = new object();

        public static void Write(string level, string msg)
        {
            try
            {
                lock (Gate)
                {
                    Paths.EnsureDir();
                    string file = Path.Combine(Paths.LogDir, "batterycare-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                    string line = string.Format("{0} [{1}] {2}", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), level, msg);
                    File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }

        public static void Info(string m) { Write("INFO", m); }
        public static void Warn(string m) { Write("WARN", m); }
        public static void Error(string m) { Write("ERROR", m); }
    }

    // ---------------------------------------------------------------- ACPI bridge

    static class Atk
    {
        const uint IOCTL = 0x0022240C;
        const uint DSTS = 0x53545344;
        const uint DEVS = 0x53564544;
        public const uint DeviceBatteryLimit = 0x00120057;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inBuf, uint inSize, byte[] outBuf, uint outSize, ref uint ret, IntPtr ov);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr h);

        static uint[] Call(uint method, uint[] args)
        {
            IntPtr h = CreateFile(@"\\.\ATKACPI", 0x80000000u | 0x40000000u, 3u, IntPtr.Zero, 3u, 0x80u, IntPtr.Zero);
            if (h == new IntPtr(-1))
                throw new InvalidOperationException("ATKACPI 裝置開啟失敗 (Win32=" + Marshal.GetLastWin32Error() + ")");
            try
            {
                byte[] inBuf = new byte[8 + args.Length * 4];
                BitConverter.GetBytes(method).CopyTo(inBuf, 0);
                BitConverter.GetBytes((uint)(args.Length * 4)).CopyTo(inBuf, 4);
                for (int i = 0; i < args.Length; i++)
                    BitConverter.GetBytes(args[i]).CopyTo(inBuf, 8 + i * 4);

                byte[] outBuf = new byte[16];
                uint ret = 0;
                if (!DeviceIoControl(h, IOCTL, inBuf, (uint)inBuf.Length, outBuf, (uint)outBuf.Length, ref ret, IntPtr.Zero))
                    throw new InvalidOperationException("ATKACPI ioctl 失敗 (Win32=" + Marshal.GetLastWin32Error() + ")");

                return new uint[]
                {
                    BitConverter.ToUInt32(outBuf, 0),
                    BitConverter.ToUInt32(outBuf, 4),
                    BitConverter.ToUInt32(outBuf, 8),
                    BitConverter.ToUInt32(outBuf, 12)
                };
            }
            finally { CloseHandle(h); }
        }

        public static uint RawLimitStatus() { return Call(DSTS, new[] { DeviceBatteryLimit })[0]; }

        public static int SetLimit(int percent)
        {
            return (int)Call(DEVS, new[] { DeviceBatteryLimit, (uint)percent })[0];
        }
    }

    // ---------------------------------------------------------------- power info

    static class Power
    {
        // debug/test only: pretend the AC adapter is unplugged
        public static bool SimulateDc;

        public static bool OnAc()
        {
            if (SimulateDc) return false;
            try { return SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online; }
            catch { return true; }
        }

        public static int Percent()
        {
            try
            {
                float f = SystemInformation.PowerStatus.BatteryLifePercent;
                if (float.IsNaN(f)) return -1;
                int p = (int)Math.Round(f * 100f);
                return p < 0 || p > 100 ? -1 : p;
            }
            catch { return -1; }
        }

        public static bool Charging()
        {
            try
            {
                return (SystemInformation.PowerStatus.BatteryChargeStatus & BatteryChargeStatus.Charging) != 0;
            }
            catch { return false; }
        }

        public static bool NoBattery()
        {
            try
            {
                return (SystemInformation.PowerStatus.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) != 0;
            }
            catch { return false; }
        }

        public static string Describe()
        {
            int p = Percent();
            string power = OnAc() ? "插電中" : "使用電池";
            return (p >= 0 ? "電量 " + p + "%" : "電量 --") + " ｜ " + power;
        }
    }

    // ---------------------------------------------------------------- config

    class Config
    {
        readonly object gate = new object();

        public string Mode = "care";
        public string LeaseId = "";
        public DateTime LeaseStartUtc = DateTime.MinValue;
        public DateTime DeadlineUtc = DateTime.MinValue;
        public int AppliedLimit;
        public string LastError = "";
        public DateTime LastSeenUtc = DateTime.MinValue;
        public DateTime AppliedAtUtc = DateTime.MinValue;
        public int LeaseMinutes = 120;

        public object Gate { get { return gate; } }

        static string Fmt(DateTime d)
        {
            if (d == DateTime.MinValue) return "";
            return d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        static DateTime ParseUtc(string s)
        {
            DateTime d;
            if (!string.IsNullOrEmpty(s) && DateTime.TryParseExact(s, "yyyy-MM-dd'T'HH:mm:ss'Z'",
                    CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d))
                return DateTime.SpecifyKind(d, DateTimeKind.Utc);
            return DateTime.MinValue;
        }

        public static Config Load()
        {
            var c = new Config();
            try
            {
                if (!File.Exists(Paths.ConfigFile)) return c;
                foreach (string raw in File.ReadAllLines(Paths.ConfigFile))
                {
                    int eq = raw.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = raw.Substring(0, eq).Trim();
                    string v = raw.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "Mode": c.Mode = v == "full" ? "full" : "care"; break;
                        case "LeaseId": c.LeaseId = v; break;
                        case "LeaseStartUtc": c.LeaseStartUtc = ParseUtc(v); break;
                        case "DeadlineUtc": c.DeadlineUtc = ParseUtc(v); break;
                        case "AppliedLimit": int.TryParse(v, out c.AppliedLimit); break;
                        case "LastError": c.LastError = v; break;
                        case "LastSeenUtc": c.LastSeenUtc = ParseUtc(v); break;
                        case "AppliedAtUtc": c.AppliedAtUtc = ParseUtc(v); break;
                        case "LeaseMinutes": int.TryParse(v, out c.LeaseMinutes); break;
                    }
                }
                if (c.LeaseMinutes < 5) c.LeaseMinutes = 120;
            }
            catch (Exception ex) { Log.Warn("config.load: " + ex.Message); }
            return c;
        }

        public void Save()
        {
            lock (gate)
            {
                try
                {
                    Paths.EnsureDir();
                    var sb = new StringBuilder();
                    sb.AppendLine("SchemaVersion=1");
                    sb.AppendLine("Mode=" + Mode);
                    sb.AppendLine("LeaseId=" + LeaseId);
                    sb.AppendLine("LeaseStartUtc=" + Fmt(LeaseStartUtc));
                    sb.AppendLine("DeadlineUtc=" + Fmt(DeadlineUtc));
                    sb.AppendLine("AppliedLimit=" + AppliedLimit);
                    sb.AppendLine("LastError=" + LastError.Replace("\r", " ").Replace("\n", " "));
                    sb.AppendLine("LastSeenUtc=" + Fmt(LastSeenUtc));
                    sb.AppendLine("AppliedAtUtc=" + Fmt(AppliedAtUtc));
                    sb.AppendLine("LeaseMinutes=" + LeaseMinutes);
                    string tmp = Paths.ConfigFile + ".tmp";
                    File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
                    if (File.Exists(Paths.ConfigFile)) File.Delete(Paths.ConfigFile);
                    File.Move(tmp, Paths.ConfigFile);
                }
                catch (Exception ex) { Log.Warn("config.save: " + ex.Message); }
            }
        }
    }

    // ---------------------------------------------------------------- controller

    class Controller
    {
        readonly Config cfg;
        readonly BlockingCollection<Action> queue = new BlockingCollection<Action>();
        Thread worker;

        public volatile bool Pending;
        public volatile bool LastApplyOk = true;
        public event Action Changed;
        public event Action<string> Notify;

        DateTime lastDriftUtc = DateTime.MinValue;
        DateTime lastApplyUtc = DateTime.MinValue;
        DateTime lastSeenSaveUtc = DateTime.MinValue;
        bool faultNotified;

        public Controller(Config c) { cfg = c; }

        public Config Cfg { get { return cfg; } }

        public void Start()
        {
            worker = new Thread(Loop) { IsBackground = false, Name = "battery-care-worker" };
            worker.Start();
        }

        public void Stop()
        {
            try { queue.CompleteAdding(); } catch { }
        }

        void Loop()
        {
            foreach (Action a in queue.GetConsumingEnumerable())
            {
                try { a(); }
                catch (Exception ex) { Log.Error("worker: " + ex.GetType().Name + ": " + ex.Message); }
            }
        }

        public void Post(Action a)
        {
            try { queue.Add(a); } catch { }
        }

        public void RaiseChanged() { var h = Changed; if (h != null) h(); }

        // --- public commands (called from UI thread)

        public void RequestApply(string reason, bool force)
        {
            Pending = true;
            RaiseChanged();
            Post(delegate { DoApply(reason, force); });
        }

        public void StartFullLease()
        {
            lock (cfg.Gate)
            {
                DateTime now = DateTime.UtcNow;
                cfg.Mode = "full";
                cfg.LeaseId = Guid.NewGuid().ToString("N");
                cfg.LeaseStartUtc = now;
                cfg.DeadlineUtc = now.AddMinutes(cfg.LeaseMinutes);
                cfg.Save();
                Log.Info("lease.start id=" + cfg.LeaseId + " deadline=" + cfg.DeadlineUtc.ToString("o") + " minutes=" + cfg.LeaseMinutes);
            }
            RequestApply("user-full", true);
        }

        public void GoCare(string reason)
        {
            Revoke(reason);
            RequestApply("user-care", true);
        }

        void Revoke(string reason)
        {
            lock (cfg.Gate)
            {
                if (cfg.Mode == "full" || !string.IsNullOrEmpty(cfg.LeaseId))
                    Log.Info("lease.revoke reason=" + reason);
                cfg.Mode = "care";
                cfg.LeaseId = "";
                cfg.LeaseStartUtc = DateTime.MinValue;
                cfg.DeadlineUtc = DateTime.MinValue;
                cfg.Save();
            }
        }

        // --- worker-side logic

        int ResolveDesired(out string note)
        {
            note = null;
            lock (cfg.Gate)
            {
                if (cfg.Mode == "full")
                {
                    if (string.IsNullOrEmpty(cfg.LeaseId)) { Revoke("no-lease"); note = "狀態資料異常，已回到保養 80%。"; }
                    else if (DateTime.UtcNow >= cfg.DeadlineUtc) { Revoke("deadline"); note = "已到 " + cfg.LeaseMinutes + " 分鐘上限，回到保養 80%。"; }
                    else if (!Power.OnAc()) { Revoke("unplugged"); note = "偵測到拔除電源，回到保養 80%。"; }
                    else if (cfg.LastSeenUtc != DateTime.MinValue && DateTime.UtcNow < cfg.LastSeenUtc.AddMinutes(-10)) { Revoke("clock"); note = "系統時間異常，保守回到保養 80%。"; }
                }
                return cfg.Mode == "full" ? 100 : 80;
            }
        }

        void DoApply(string reason, bool force)
        {
            try
            {
                string note;
                int desired = ResolveDesired(out note);
                if (!force && cfg.AppliedLimit == desired && cfg.LastError.Length == 0)
                {
                    return;
                }

                int result = 0;
                string error = null;
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    try { result = Atk.SetLimit(desired); error = null; }
                    catch (Exception ex) { result = -1; error = ex.Message; }
                    if (result == 1) break;
                    Log.Warn(string.Format("devs.fail target={0} result={1} attempt={2}/3 err={3}", desired, result, attempt, error));
                    if (attempt < 3) Thread.Sleep(attempt * 1000);
                }

                if (result == 1)
                {
                    lock (cfg.Gate)
                    {
                        cfg.AppliedLimit = desired;
                        cfg.AppliedAtUtc = DateTime.UtcNow;
                        cfg.LastError = "";
                        cfg.Save();
                    }
                    LastApplyOk = true;
                    faultNotified = false;
                    lastApplyUtc = DateTime.UtcNow;
                    Log.Info(string.Format("devs.ok target={0} reason={1}", desired, reason));
                    var n = Notify;
                    if (n != null)
                    {
                        if (note != null) n(note);
                        else if (reason == "user-full") n("已解除限充，開始充到 100%。（拔電或 2 小時後自動回保養）");
                        else if (reason == "user-care") n("已回到保養 80%。");
                    }
                }
                else
                {
                    lock (cfg.Gate)
                    {
                        cfg.LastError = error != null ? error : ("result=" + result);
                        cfg.Save();
                    }
                    LastApplyOk = false;
                    Log.Error(string.Format("devs.giveup target={0} reason={1} err={2}", desired, reason, cfg.LastError));
                    if (!faultNotified)
                    {
                        faultNotified = true;
                        var n = Notify;
                        if (n != null) n("切換失敗：" + cfg.LastError + "。目前維持 " + cfg.AppliedLimit + "%。");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("apply: " + ex.Message);
            }
            finally
            {
                Pending = false;
                RaiseChanged();
            }
        }

        /// <summary>Runs on the worker thread: deadline / unplug / drift maintenance.</summary>
        public void Maintenance(string reason)
        {
            try
            {
                DateTime now = DateTime.UtcNow;
                lock (cfg.Gate) { cfg.LastSeenUtc = now; }
                if (lastSeenSaveUtc == DateTime.MinValue || (now - lastSeenSaveUtc).TotalSeconds >= 60)
                {
                    lastSeenSaveUtc = now;
                    lock (cfg.Gate) cfg.Save();
                }

                string note;
                int desired = ResolveDesired(out note);
                if (note != null) Log.Info("maintenance.revoke reason=" + reason);

                bool onAc = Power.OnAc();
                int pct = Power.Percent();
                bool charging = Power.Charging();

                bool drift = false;
                if (onAc && pct >= 0)
                {
                    // care mode but the machine is still charging past the limit -> limit got lifted elsewhere
                    if (desired == 80 && pct >= 82 && charging) drift = true;
                    // full mode but charging stopped before 100 -> limit got pushed back down
                    if (desired == 100 && pct < 99 && !charging) drift = true;
                }

                bool needApply = cfg.AppliedLimit != desired;

                if (drift && (DateTime.UtcNow - lastApplyUtc).TotalSeconds > 90
                         && (DateTime.UtcNow - lastDriftUtc).TotalSeconds > 60)
                {
                    lastDriftUtc = DateTime.UtcNow;
                    Log.Warn(string.Format("drift.detected desired={0} pct={1} charging={2} applied={3}", desired, pct, charging, cfg.AppliedLimit));
                    DoApply("drift", true);
                }
                else if (needApply)
                {
                    DoApply(reason, false);
                }
            }
            catch (Exception ex) { Log.Error("maintenance: " + ex.Message); }
        }

        public void ResetLastSeen()
        {
            lock (cfg.Gate)
            {
                if (cfg.Mode == "full" && cfg.LastSeenUtc != DateTime.MinValue && DateTime.UtcNow < cfg.LastSeenUtc.AddMinutes(-10))
                {
                    Log.Warn("clock.rollback detected at startup");
                    Revoke("clock-startup");
                }
            }
        }
    }

    // ---------------------------------------------------------------- startup (HKCU Run)

    static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "BatteryCareTray";

        /// <summary>Set by the PowerShell host wrapper; overrides the default exe-based command.</summary>
        public static string CommandOverride;

        public static string ExePath { get { return Application.ExecutablePath; } }

        public static bool Enabled
        {
            get
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                    {
                        if (k == null) return false;
                        var v = k.GetValue(ValueName) as string;
                        return !string.IsNullOrEmpty(v);
                    }
                }
                catch { return false; }
            }
        }

        public static void Set(bool enabled)
        {
            string command = CommandOverride;
            if (string.IsNullOrEmpty(command)) command = "\"" + ExePath + "\" --autostart";
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (k == null) throw new InvalidOperationException("無法開啟 Run 機碼");
                if (enabled) k.SetValue(ValueName, command);
                else if (k.GetValue(ValueName) != null) k.DeleteValue(ValueName, false);
            }
            Log.Info("startup." + (enabled ? "enable" : "disable") + " cmd=" + command);
        }
    }

    // ---------------------------------------------------------------- tray icon placement (Win11)

    static class TrayPromoter
    {
        const string Root = @"Control Panel\NotifyIconSettings";

        /// <summary>
        /// Windows 11 keeps new tray icons on the taskbar. This tool is meant to live inside the "^"
        /// overflow, so force IsPromoted=0 for our own entry.
        /// </summary>
        public static bool EnsureHidden()
        {
            try
            {
                using (RegistryKey root = Registry.CurrentUser.OpenSubKey(Root))
                {
                    if (root == null) return false;
                    string exe = Application.ExecutablePath;
                    bool touched = false;
                    // Prefer the entry that carries our own tooltip; never touch unrelated entries of the same host exe.
                    foreach (string name in root.GetSubKeyNames())
                    {
                        using (RegistryKey k = root.OpenSubKey(name, true))
                        {
                            if (k == null) continue;
                            string path = k.GetValue("ExecutablePath") as string;
                            if (string.IsNullOrEmpty(path)) continue;
                            if (!string.Equals(path, exe, StringComparison.OrdinalIgnoreCase)) continue;
                            string tooltip = k.GetValue("InitialTooltip") as string;
                            bool mine = !string.IsNullOrEmpty(tooltip)
                                        && (tooltip.Contains("保養") || tooltip.Contains("充到 100") || tooltip.Contains("電量") || tooltip.Contains("切換中"));
                            if (!mine) continue;
                            object v = k.GetValue("IsPromoted");
                            int current = (v is int) ? (int)v : 0;
                            if (current != 0)
                            {
                                k.SetValue("IsPromoted", 0, RegistryValueKind.DWord);
                                Log.Info("tray.hide-overflow key=" + name);
                            }
                            touched = true;
                        }
                    }
                    return touched;
                }
            }
            catch (Exception ex) { Log.Warn("tray.hide: " + ex.Message); }
            return false;
        }
    }

    // ---------------------------------------------------------------- tray UI

    class MessageWindow : Form
    {
        public event Action<int> PowerEvent;
        public event Action TaskbarCreated;
        public event Action<int> Command;

        public MessageWindow()
        {
            Text = Forward.WindowTitle;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Minimized;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-4000, -4000);
            Size = new Size(1, 1);
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(false);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0218)
            {
                var h = PowerEvent;
                if (h != null) h(m.WParam.ToInt32());
                return;
            }
            if (Native.WM_CMD != 0 && m.Msg == Native.WM_CMD)
            {
                var h2 = Command;
                if (h2 != null) h2(m.WParam.ToInt32());
                return;
            }
            if (Native.WM_TASKBARCREATED != 0 && m.Msg == Native.WM_TASKBARCREATED)
            {
                var h = TaskbarCreated;
                if (h != null) h();
                return;
            }
            base.WndProc(ref m);
        }
    }

    static class Native
    {
        public static readonly int WM_TASKBARCREATED = RegisterWindowMessage("TaskbarCreated");
        public static readonly int WM_CMD = RegisterWindowMessage("BatteryCareTray.Command");

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int RegisterWindowMessage(string msg);

        public static readonly Guid GUID_ACDC_POWER_SOURCE =
            new Guid("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid guid, int flags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterPowerSettingNotification(IntPtr handle);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);
    }

    /// <summary>Sends a command (80 / 100 / 199=quit) to an already running tray instance.</summary>
    static class Forward
    {
        public const string WindowTitle = "BatteryCareTray.Hidden";

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public static bool Send(int command)
        {
            try
            {
                int msg = Native.WM_CMD;
                if (msg == 0) return false;
                IntPtr h = FindWindow(null, WindowTitle);
                if (h == IntPtr.Zero) return false;
                return PostMessage(h, msg, (IntPtr)command, IntPtr.Zero);
            }
            catch (Exception ex) { Log.Warn("forward: " + ex.Message); return false; }
        }
    }

    class TrayApp : IDisposable
    {
        readonly Config cfg;
        readonly Controller controller;
        readonly NotifyIcon icon;
        readonly ContextMenuStrip menu;
        readonly MessageWindow window;
        readonly System.Windows.Forms.Timer tickTimer;
        readonly System.Windows.Forms.Timer debounceTimer;

        ToolStripMenuItem statusItem;
        ToolStripMenuItem modeItem;
        ToolStripMenuItem failItem;
        ToolStripMenuItem fullItem;
        ToolStripMenuItem careItem;
        ToolStripMenuItem autoItem;

        IntPtr powerNotify;
        IntPtr iconHandle = IntPtr.Zero;
        readonly List<IntPtr> retiredIcons = new List<IntPtr>();
        string lastTooltip = "";
        int lastIconPctBucket = -1;
        bool lastIconFull;

        public TrayApp(Config c, bool firstRun)
        {
            cfg = c;
            controller = new Controller(c);
            controller.Changed += OnControllerChanged;
            controller.Notify += OnNotify;

            menu = new ContextMenuStrip();
            menu.Opening += OnMenuOpening;

            statusItem = new ToolStripMenuItem("電量 --");
            statusItem.Enabled = false;

            modeItem = new ToolStripMenuItem("模式：保養 80%");
            modeItem.Enabled = false;

            failItem = new ToolStripMenuItem("⚠ 上次切換失敗");
            failItem.Enabled = false;
            failItem.Visible = false;

            fullItem = new ToolStripMenuItem("充到 100%（這次）");
            fullItem.Click += delegate { controller.StartFullLease(); };

            careItem = new ToolStripMenuItem("回到保養 80%");
            careItem.Click += delegate { controller.GoCare("uicmd-care"); };

            autoItem = new ToolStripMenuItem("開機自動常駐");
            autoItem.CheckOnClick = false;
            autoItem.Click += OnToggleAutostart;

            var showIcon = new ToolStripMenuItem("把圖示收進 ^");
            showIcon.Click += delegate
            {
                if (TrayPromoter.EnsureHidden()) OnNotify("已把圖示收進 ^ 溢出區（點 ^ 可以展開）。");
                else OnNotify("找不到系統匣設定，請手動把圖示拖進 ^。");
            };

            var openFolder = new ToolStripMenuItem("開啟資料夾");
            openFolder.Click += delegate
            {
                try { System.Diagnostics.Process.Start("explorer.exe", "\"" + Paths.Dir + "\""); } catch { }
            };

            var quit = new ToolStripMenuItem("結束");
            quit.Click += delegate { ExitApp(); };

            menu.Items.Add(statusItem);
            menu.Items.Add(modeItem);
            menu.Items.Add(failItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(fullItem);
            menu.Items.Add(careItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(autoItem);
            menu.Items.Add(showIcon);
            menu.Items.Add(openFolder);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(quit);

            window = new MessageWindow();
            window.PowerEvent += OnPowerEvent;
            window.TaskbarCreated += OnTaskbarCreated;
            window.Command += OnCommand;
            // force handle creation so power notifications can be registered
            IntPtr h = window.Handle;

            Guid acdc = Native.GUID_ACDC_POWER_SOURCE;
            powerNotify = Native.RegisterPowerSettingNotification(h, ref acdc, 0);
            if (powerNotify == IntPtr.Zero)
                Log.Warn("power.register failed win32=" + Marshal.GetLastWin32Error());
            else
                Log.Info("power.register ok");

            tickTimer = new System.Windows.Forms.Timer { Interval = 20000 };
            tickTimer.Tick += OnTick;

            debounceTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            debounceTimer.Tick += OnDebounceTick;

            icon = new NotifyIcon
            {
                Text = "電池保養控",
                ContextMenuStrip = menu,
                Visible = true
            };
            icon.DoubleClick += delegate
            {
                string msg = BuildTooltip();
                icon.ShowBalloonTip(2500, "電池保養控", msg, ToolTipIcon.Info);
            };

            controller.Start();
            RefreshUi();
            tickTimer.Start();

            // keep the icon inside the "^" overflow (retry once: explorer creates the settings key late)
            TrayPromoter.EnsureHidden();
            var hideRetry = new System.Windows.Forms.Timer { Interval = 4000 };
            hideRetry.Tick += delegate
            {
                hideRetry.Stop();
                TrayPromoter.EnsureHidden();
                hideRetry.Dispose();
            };
            hideRetry.Start();

            // startup reconciliation
            Log.Info("startup power=" + (Power.OnAc() ? "AC" : "DC") + " pct=" + Power.Percent()
                     + " mode=" + cfg.Mode + " applied=" + cfg.AppliedLimit + " error=" + cfg.LastError);
            controller.ResetLastSeen();
            controller.RequestApply("startup", true);

            if (firstRun)
            {
                icon.ShowBalloonTip(6000, "電池保養控",
                    "已常駐在右下角系統匣的 ^ 溢出區。右鍵圖示可以「充到 100%（這次）」。", ToolTipIcon.Info);
            }
        }

        void OnToggleAutostart(object sender, EventArgs e)
        {
            try
            {
                Startup.Set(!Startup.Enabled);
                OnNotify(Startup.Enabled ? "已設定開機自動常駐。" : "已取消開機自動常駐。");
            }
            catch (Exception ex)
            {
                Log.Error("startup.toggle: " + ex.Message);
                OnNotify("設定開機啟動失敗：" + ex.Message);
            }
            RefreshUi();
        }

        void OnControllerChanged()
        {
            try { window.BeginInvoke(new Action(RefreshUi)); } catch { }
        }

        void OnNotify(string message)
        {
            try
            {
                window.BeginInvoke(new Action(delegate
                {
                    try
                    {
                        Log.Info("notify: " + message);
                        icon.ShowBalloonTip(4000, "電池保養控", Truncate(message, 120), ToolTipIcon.Info);
                    }
                    catch { }
                }));
            }
            catch { }
        }

        static string Truncate(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n - 1) + "…";
        }

        void OnPowerEvent(int wParam)
        {
            bool resume = wParam == 0x0012 || wParam == 0x0007;
            Log.Info("power.event wparam=0x" + wParam.ToString("X4") + (resume ? " (resume)" : ""));
            debounceTimer.Stop();
            debounceTimer.Interval = resume ? 4000 : 1500;
            debounceTimer.Start();
        }

        void OnDebounceTick(object sender, EventArgs e)
        {
            debounceTimer.Stop();
            controller.Post(delegate { controller.Maintenance("power"); });
            RefreshUi();
        }

        void OnTick(object sender, EventArgs e)
        {
            controller.Post(delegate { controller.Maintenance("tick"); });
            RefreshUi();
        }

        void OnCommand(int cmd)
        {
            Log.Info("tray.command " + cmd);
            if (cmd == 100) controller.StartFullLease();
            else if (cmd == 80) controller.GoCare("cmd-care");
            else if (cmd == 199) ExitApp();
        }

        void OnTaskbarCreated()
        {
            Log.Info("taskbar.created -> re-add tray icon");
            try
            {
                icon.Visible = false;
                icon.Visible = true;
                RefreshUi();
            }
            catch (Exception ex) { Log.Warn("taskbar.re-add: " + ex.Message); }
        }

        void OnMenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            RefreshUi();
        }

        // --- state -> UI

        string BuildTooltip()
        {
            string head = Power.Describe();
            string mode;
            bool warn = false;
            lock (cfg.Gate)
            {
                warn = !string.IsNullOrEmpty(cfg.LastError);
                if (controller.Pending) mode = "切換中…";
                else if (cfg.Mode == "full")
                {
                    TimeSpan left = cfg.DeadlineUtc - DateTime.UtcNow;
                    if (left < TimeSpan.Zero) left = TimeSpan.Zero;
                    mode = "充到 100% 剩 " + (int)left.TotalMinutes + " 分";
                }
                else mode = "保養 80%";
            }
            return Truncate((warn ? "⚠ " : "") + head + " ｜ " + mode, 62);
        }

        void RefreshUi()
        {
            try
            {
                string tooltip = BuildTooltip();
                if (tooltip != lastTooltip)
                {
                    lastTooltip = tooltip;
                    icon.Text = tooltip;
                }

                int pct = Power.Percent();
                bool full;
                lock (cfg.Gate) full = cfg.Mode == "full";
                UpdateIcon(pct, full);

                // menu text
                statusItem.Text = "🔋 " + Power.Describe();
                lock (cfg.Gate)
                {
                    if (cfg.Mode == "full")
                    {
                        TimeSpan left = cfg.DeadlineUtc - DateTime.UtcNow;
                        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
                        modeItem.Text = string.Format("目前：充到 100%（剩 {0:D2}:{1:D2}）", (int)left.TotalMinutes, left.Seconds);
                    }
                    else modeItem.Text = "目前：保養 80%";

                    failItem.Visible = !string.IsNullOrEmpty(cfg.LastError);
                    if (failItem.Visible) failItem.Text = "⚠ 上次切換失敗：" + Truncate(cfg.LastError, 40);

                    bool pending = controller.Pending;
                    fullItem.Enabled = !pending && cfg.Mode != "full";
                    careItem.Enabled = !pending && (cfg.Mode == "full" || cfg.LastError.Length > 0);
                    modeItem.Text = pending ? "切換中…" : modeItem.Text;
                }
                autoItem.Checked = Startup.Enabled;
            }
            catch (Exception ex) { Log.Warn("ui.refresh: " + ex.Message); }
        }

        void UpdateIcon(int pct, bool full)
        {
            int bucket = pct < 0 ? -1 : pct / 10;
            if (bucket == lastIconPctBucket && full == lastIconFull) return;
            lastIconPctBucket = bucket;
            lastIconFull = full;
            try
            {
                Bitmap bmp = DrawBattery(pct, full);
                IntPtr old = iconHandle;
                IntPtr handle = bmp.GetHicon();
                icon.Icon = Icon.FromHandle(handle);
                iconHandle = handle;
                bmp.Dispose();
                if (old != IntPtr.Zero) retiredIcons.Add(old);
            }
            catch (Exception ex) { Log.Warn("icon.draw: " + ex.Message); }
        }

        static Bitmap DrawBattery(int pct, bool full)
        {
            var bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                Color fill = full ? Color.FromArgb(244, 156, 34) : Color.FromArgb(68, 190, 108);
                Color line = Color.FromArgb(220, 220, 220);

                int bodyX = 3, bodyY = 8, bodyW = 22, bodyH = 16;
                using (var pen = new Pen(line, 2f))
                using (var brush = new SolidBrush(fill))
                {
                    int innerW = bodyW - 6;
                    int fillW = pct < 0 ? 0 : (int)Math.Round(innerW * Math.Min(100, Math.Max(0, pct)) / 100.0);
                    if (fillW > 0) g.FillRectangle(brush, bodyX + 3, bodyY + 3, fillW, bodyH - 6);
                    g.DrawRectangle(pen, bodyX, bodyY, bodyW, bodyH);
                    using (var cap = new SolidBrush(line)) g.FillRectangle(cap, bodyX + bodyW + 1, bodyY + 5, 3, 6);
                }
                if (full)
                {
                    using (var b = new SolidBrush(Color.FromArgb(255, 214, 120)))
                        g.FillEllipse(b, 20, 1, 10, 10);
                }
            }
            return bmp;
        }

        void ExitApp()
        {
            Log.Info("exit by user");
            try { tickTimer.Stop(); debounceTimer.Stop(); } catch { }
            try { icon.Visible = false; } catch { }
            controller.Stop();
            if (powerNotify != IntPtr.Zero)
            {
                Native.UnregisterPowerSettingNotification(powerNotify);
                powerNotify = IntPtr.Zero;
            }
            if (iconHandle != IntPtr.Zero) { Native.DestroyIcon(iconHandle); iconHandle = IntPtr.Zero; }
            foreach (IntPtr h in retiredIcons) { try { Native.DestroyIcon(h); } catch { } }
            retiredIcons.Clear();
            icon.Dispose();
            try { window.Close(); } catch { }
            Application.Exit();
        }

        public void Run()
        {
            Application.Run(window);
        }

        public void Dispose()
        {
            try { icon.Dispose(); } catch { }
            try { menu.Dispose(); } catch { }
        }
    }

    // ---------------------------------------------------------------- CLI

    static class Cli
    {
        public static int Run(string[] args, Config cfg)
        {
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch { }
            try
            {
                string cmd = args[0].ToLowerInvariant();
                switch (cmd)
                {
                    case "--status": return Status(cfg);
                    case "--probe": return Probe();
                    case "--maintenance": return Maintenance(cfg, args);
                    case "--install": return Install(cfg);
                    case "--uninstall": return Uninstall();
                    case "--set": return SetLimit(args, cfg);
                    case "--help": case "-h": case "/?": PrintHelp(); return 0;
                    default: Console.Error.WriteLine("未知參數: " + args[0]); PrintHelp(); return 2;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FATAL: " + ex.Message);
                return 1;
            }
        }

        static void PrintHelp()
        {
            Console.WriteLine("用法: battery-tray.exe [--status | --probe | --set 80|100 [--lease-minutes N]]");
        }

        static int Probe()
        {
            Console.WriteLine("exe=" + Application.ExecutablePath);
            Console.WriteLine("admin=" + IsAdmin());
            Console.WriteLine("data_dir=" + Paths.Dir);
            Console.WriteLine("reachable=" + Reachable());
            try { Console.WriteLine("dsts_raw=0x" + Atk.RawLimitStatus().ToString("X8")); }
            catch (Exception ex) { Console.WriteLine("dsts_error=" + ex.Message); }
            Console.WriteLine("battery=" + Power.Describe() + " charging=" + Power.Charging());
            Console.WriteLine("startup_run_key=" + Startup.Enabled);
            return 0;
        }

        static bool IsAdmin()
        {
            try
            {
                var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                var p = new System.Security.Principal.WindowsPrincipal(id);
                return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        public static bool Reachable()
        {
            try { Atk.RawLimitStatus(); return true; }
            catch { return false; }
        }

        static int Status(Config cfg)
        {
            Console.WriteLine("mode=" + cfg.Mode);
            Console.WriteLine("applied=" + cfg.AppliedLimit);
            Console.WriteLine("lease_id=" + cfg.LeaseId);
            Console.WriteLine("lease_start_utc=" + (cfg.LeaseStartUtc == DateTime.MinValue ? "" : cfg.LeaseStartUtc.ToString("o")));
            Console.WriteLine("deadline_utc=" + (cfg.DeadlineUtc == DateTime.MinValue ? "" : cfg.DeadlineUtc.ToString("o")));
            long remaining = cfg.Mode == "full" ? (long)(cfg.DeadlineUtc - DateTime.UtcNow).TotalSeconds : 0;
            if (cfg.Mode == "full" && remaining < 0) remaining = 0;
            Console.WriteLine("remaining_sec=" + remaining);
            Console.WriteLine("power=" + (Power.OnAc() ? "AC" : "DC"));
            Console.WriteLine("pct=" + Power.Percent());
            Console.WriteLine("charging=" + Power.Charging());
            Console.WriteLine("last_error=" + cfg.LastError);
            Console.WriteLine("startup=" + Startup.Enabled);
            Console.WriteLine("tray=" + (TrayRunning() ? "running" : "stopped"));
            Console.WriteLine("config=" + Paths.ConfigFile);
            Console.WriteLine("log_dir=" + Paths.LogDir);
            return 0;
        }

        static bool TrayRunning()
        {
            try
            {
                using (Mutex m = Mutex.OpenExisting("Local\\BatteryCareTray"))
                {
                    return m != null;
                }
            }
            catch { return false; }
        }

        static int Maintenance(Config cfg, string[] args)
        {
            if (TrayRunning())
            {
                Console.Error.WriteLine("常駐程式正在執行，請改用托盤選單（或先在工作管理員結束 battery-tray.exe）。");
                return 8;
            }
            if (Array.IndexOf(args, "--simulate-dc") >= 0) Power.SimulateDc = true;
            var ctrl = new Controller(cfg);
            ctrl.Maintenance("cli");
            Console.WriteLine("--- after maintenance ---");
            return Status(cfg);
        }

        static int Install(Config cfg)
        {
            if (TrayRunning())
            {
                Console.Error.WriteLine("常駐程式正在執行，請先結束再安裝。");
                return 8;
            }
            try { Startup.Set(true); }
            catch (Exception ex) { Console.Error.WriteLine("startup failed: " + ex.Message); return 1; }
            Console.WriteLine("startup=enabled");
            lock (cfg.Gate)
            {
                cfg.Mode = "care";
                cfg.LeaseId = "";
                cfg.LeaseStartUtc = DateTime.MinValue;
                cfg.DeadlineUtc = DateTime.MinValue;
                cfg.Save();
            }
            try
            {
                int result = Atk.SetLimit(80);
                if (result == 1)
                {
                    lock (cfg.Gate)
                    {
                        cfg.AppliedLimit = 80;
                        cfg.LastError = "";
                        cfg.AppliedAtUtc = DateTime.UtcNow;
                        cfg.Save();
                    }
                    Console.WriteLine("applied=80");
                    Log.Info("install.ok");
                    return 0;
                }
                Console.Error.WriteLine("applied failed result=" + result);
                return 5;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("applied failed: " + ex.Message);
                return 6;
            }
        }

        static int Uninstall()
        {
            try { Startup.Set(false); Console.WriteLine("startup=disabled"); }
            catch (Exception ex) { Console.Error.WriteLine("uninstall failed: " + ex.Message); return 1; }
            Log.Info("uninstall.ok");
            return 0;
        }

        static int SetLimit(string[] args, Config cfg)
        {
            if (args.Length < 2) { Console.Error.WriteLine("需要 --set 80 或 --set 100"); return 2; }
            int percent;
            if (!int.TryParse(args[1], out percent) || (percent != 80 && percent != 100))
            {
                Console.Error.WriteLine("只接受 80 或 100");
                return 2;
            }
            if (TrayRunning())
            {
                if (Forward.Send(percent))
                {
                    Console.WriteLine("forwarded set=" + percent + " to running tray");
                    return 0;
                }
                Console.Error.WriteLine("常駐程式正在執行但轉交失敗，請改用托盤選單。");
                return 8;
            }
            int leaseMinutes = cfg.LeaseMinutes;
            for (int i = 2; i < args.Length - 1; i++)
            {
                if (args[i] == "--lease-minutes") int.TryParse(args[i + 1], out leaseMinutes);
            }
            if (leaseMinutes < 5) leaseMinutes = 120;

            DateTime now = DateTime.UtcNow;
            lock (cfg.Gate)
            {
                if (percent == 100)
                {
                    cfg.Mode = "full";
                    cfg.LeaseId = Guid.NewGuid().ToString("N");
                    cfg.LeaseStartUtc = now;
                    cfg.DeadlineUtc = now.AddMinutes(leaseMinutes);
                    cfg.LeaseMinutes = leaseMinutes;
                }
                else
                {
                    cfg.Mode = "care";
                    cfg.LeaseId = "";
                    cfg.LeaseStartUtc = DateTime.MinValue;
                    cfg.DeadlineUtc = DateTime.MinValue;
                }
                cfg.Save();
            }

            try
            {
                int result = Atk.SetLimit(percent);
                if (result == 1)
                {
                    lock (cfg.Gate)
                    {
                        cfg.AppliedLimit = percent;
                        cfg.AppliedAtUtc = now;
                        cfg.LastError = "";
                        cfg.Save();
                    }
                    Console.WriteLine("ok set=" + percent + " mode=" + cfg.Mode + " deadline_utc=" + (cfg.DeadlineUtc == DateTime.MinValue ? "" : cfg.DeadlineUtc.ToString("o")));
                    Log.Info("cli.set " + percent + " ok");
                    return 0;
                }
                lock (cfg.Gate)
                {
                    cfg.LastError = "result=" + result;
                    cfg.Save();
                }
                Console.Error.WriteLine("fail set=" + percent + " result=" + result + " current=" + cfg.AppliedLimit);
                Log.Error("cli.set " + percent + " result=" + result);
                return 5;
            }
            catch (Exception ex)
            {
                lock (cfg.Gate)
                {
                    cfg.LastError = ex.Message;
                    cfg.Save();
                }
                Console.Error.WriteLine("fail set=" + percent + " error=" + ex.Message + " current=" + cfg.AppliedLimit);
                Log.Error("cli.set " + percent + " error=" + ex.Message);
                return 6;
            }
        }
    }

    // ---------------------------------------------------------------- entry point

    public static class Program
    {
        public static string AutostartCommand;

        [STAThread]
        public static int Main(string[] args)
        {
            if (!string.IsNullOrEmpty(AutostartCommand)) Startup.CommandOverride = AutostartCommand;
            Paths.EnsureDir();
            Log.Info("launch args=[" + string.Join(" ", args) + "] exe=" + Application.ExecutablePath);

            if (args.Length > 0 && args[0].StartsWith("--") && args[0] != "--autostart")
                return Cli.Run(args, Config.Load());

            bool firstRun = !File.Exists(Paths.ConfigFile);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e)
            {
                Log.Error("thread.exception: " + e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            {
                Log.Error("domain.exception: " + e.ExceptionObject);
            };

            bool created;
            using (var mutex = new Mutex(true, "Local\\BatteryCareTray", out created))
            {
                if (!created)
                {
                    Log.Info("second instance detected, exiting");
                    return 0;
                }

                bool autostart = Array.IndexOf(args, "--autostart") >= 0;
                if (autostart)
                {
                    // give the shell a moment at logon
                    Log.Info("autostart, waiting for shell");
                    Thread.Sleep(4000);
                }

                var cfg = Config.Load();
                using (var app = new TrayApp(cfg, firstRun))
                {
                    app.Run();
                }
                GC.KeepAlive(mutex);
            }
            return 0;
        }
    }
}
