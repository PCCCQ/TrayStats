// TrayStats.cs — 系统托盘实时状态小工具（网速 / CPU / GPU / 内存）
//
// 编译：运行 build.ps1（使用 Windows 系统自带的 .NET Framework csc.exe，无需安装任何 SDK）
// 运行：TrayStats.exe        托盘显示 3 个微型文字图标，每秒刷新
//       TrayStats.exe test   无界面自检，验证本机计数器可用性，结果写入 TrayStats_selftest.log
//
// 数据来源（均为系统自带，普通用户权限即可读取）：
//   网速  PerformanceCounter "Network Interface" 的 Bytes Sent/Received per sec，全部网卡求和
//   CPU   "Processor Information(_Total)\% Processor Utility"，缺失时回退 "\Processor(_Total)\% Processor Time"
//   GPU   "GPU Engine(*)\Utilization Percentage"，按引擎类型(3D/Copy/…)分组求和取最大并截断到 100%
//   内存  GlobalMemoryStatusEx
// 说明：.NET 的 PerformanceCounter 内部按计数器索引解析名称，不受系统语言影响。
//
// 界面：任务栏状态条 + 1 个托盘 logo 图标
//   状态条：贴在任务栏上、紧靠时钟左侧（Win11 任务栏为 XAML 架构，无法真正嵌入窗口，
//   采用与 TrafficMonitor 相同的悬浮贴靠方案），背景色实时采样任务栏自身颜色，
//   显示 ↑↓网速 / CPU / GPU / 内存，两行文字与系统时钟同尺寸；全屏应用时自动隐藏。
//   托盘图标：静态 logo，承载右键菜单（开机自启/退出）。
//   前台全屏时状态条自动隐藏，避免遮挡游戏/视频。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TrayStats
{
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            if (args != null && args.Length > 0 &&
                string.Equals(args[0], "test", StringComparison.OrdinalIgnoreCase))
            {
                RunSelfTest();
                return;
            }

            bool createdNew = false;
            Mutex mutex = new Mutex(true, "TrayStats_SingleInstance", out createdNew);
            if (!createdNew)
            {
                MessageBox.Show("TrayStats 已在运行中。", "TrayStats",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            GC.KeepAlive(mutex);
            Application.Run(new TrayContext());
        }

        // 无界面自检：验证各计数器在本机可用，结果写入日志文件（TrayStats.exe test）
        static void RunSelfTest()
        {
            List<string> lines = new List<string>();
            try
            {
                // CPU
                PerformanceCounter cpuC = null;
                try { cpuC = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total"); }
                catch
                {
                    try { cpuC = new PerformanceCounter("Processor", "% Processor Time", "_Total"); }
                    catch { }
                }
                lines.Add("CPU 计数器: " + (cpuC != null ? cpuC.CategoryName + "\\" + cpuC.CounterName : "创建失败"));

                // 网速
                List<PerformanceCounter> sent = new List<PerformanceCounter>();
                List<PerformanceCounter> recv = new List<PerformanceCounter>();
                try
                {
                    foreach (string inst in new PerformanceCounterCategory("Network Interface").GetInstanceNames())
                    {
                        try
                        {
                            sent.Add(new PerformanceCounter("Network Interface", "Bytes Sent/sec", inst));
                            recv.Add(new PerformanceCounter("Network Interface", "Bytes Received/sec", inst));
                        }
                        catch { }
                    }
                }
                catch { }
                lines.Add("网速计数器: " + (sent.Count > 0 ? "已创建（" + sent.Count + " 个网卡实例）" : "创建失败"));

                // GPU
                List<PerformanceCounter> gpu = new List<PerformanceCounter>();
                bool gpuCatOk = false;
                try
                {
                    foreach (string inst in new PerformanceCounterCategory("GPU Engine").GetInstanceNames())
                    {
                        try { gpu.Add(new PerformanceCounter("GPU Engine", "Utilization Percentage", inst)); gpuCatOk = true; }
                        catch { }
                    }
                }
                catch { }
                lines.Add("GPU 计数器: " + (gpuCatOk ? "已创建（" + gpu.Count + " 个引擎实例）" : "创建失败（界面将显示 G-）"));
                lines.Add("");

                // 预热（速率类计数器首轮返回 0）
                if (cpuC != null) cpuC.NextValue();
                foreach (PerformanceCounter pc in sent) pc.NextValue();
                foreach (PerformanceCounter pc in recv) pc.NextValue();
                foreach (PerformanceCounter pc in gpu) pc.NextValue();
                Thread.Sleep(1500);

                if (cpuC != null)
                {
                    double cpu = cpuC.NextValue();
                    lines.Add("CPU 占用: " + (cpu >= 0 ? cpu.ToString("0.0") + "%" : "无效"));
                }

                double up = 0, down = 0;
                foreach (PerformanceCounter pc in sent) up += pc.NextValue();
                foreach (PerformanceCounter pc in recv) down += pc.NextValue();
                lines.Add("上行速率: " + up.ToString("0") + " B/s");
                lines.Add("下行速率: " + down.ToString("0") + " B/s");

                Dictionary<string, double> sums = new Dictionary<string, double>();
                foreach (PerformanceCounter pc in gpu)
                {
                    try
                    {
                        string t = Util.EngineType(pc.InstanceName);
                        double cur;
                        sums.TryGetValue(t, out cur);
                        sums[t] = cur + pc.NextValue();
                    }
                    catch { }
                }
                double gpuMax = 0;
                foreach (KeyValuePair<string, double> kv in sums)
                {
                    if (kv.Value > gpuMax) gpuMax = kv.Value;
                    lines.Add("  GPU " + kv.Key + " = " + kv.Value.ToString("0.0") + "%");
                }
                lines.Add("GPU 总占用（取最大引擎）: " + gpuMax.ToString("0.0") + "%");

                Kernel32.MEMORYSTATUSEX ms = new Kernel32.MEMORYSTATUSEX();
                if (Kernel32.GlobalMemoryStatusEx(ms))
                    lines.Add("内存占用: " + ms.dwMemoryLoad + "%");

                lines.Add("");
                lines.Add("自检完成，时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            }
            catch (Exception ex)
            {
                lines.Add("自检异常: " + ex.Message);
            }
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TrayStats_selftest.log");
            File.WriteAllLines(path, lines.ToArray());
        }
    }

    // ---------- 数据采集 ----------

    // 网速：全部网卡实例求和。网卡列表变化时自动重建。
    sealed class NetCollector : IDisposable
    {
        readonly List<PerformanceCounter> _sent = new List<PerformanceCounter>();
        readonly List<PerformanceCounter> _recv = new List<PerformanceCounter>();
        bool _warm;
        double _lastUp = double.NaN, _lastDown = double.NaN;

        public double Up { get { return _lastUp; } }
        public double Down { get { return _lastDown; } }

        public void Sample()
        {
            try
            {
                if (_sent.Count == 0) Build();
                if (_sent.Count == 0) return;
                if (!_warm)
                {
                    foreach (PerformanceCounter pc in _sent) pc.NextValue();
                    foreach (PerformanceCounter pc in _recv) pc.NextValue();
                    _warm = true;
                    return;
                }
                double u = 0, d = 0;
                bool bad = false;
                for (int i = 0; i < _sent.Count; i++)
                {
                    try { u += _sent[i].NextValue(); d += _recv[i].NextValue(); }
                    catch (InvalidOperationException) { bad = true; }
                }
                if (bad) Clear();   // 网卡实例变化，下轮重建
                _lastUp = u;
                _lastDown = d;
            }
            catch { Clear(); }
        }

        void Build()
        {
            try
            {
                foreach (string inst in new PerformanceCounterCategory("Network Interface").GetInstanceNames())
                {
                    try
                    {
                        _sent.Add(new PerformanceCounter("Network Interface", "Bytes Sent/sec", inst));
                        _recv.Add(new PerformanceCounter("Network Interface", "Bytes Received/sec", inst));
                    }
                    catch { }
                }
            }
            catch { }
        }

        void Clear()
        {
            foreach (PerformanceCounter pc in _sent) pc.Dispose();
            foreach (PerformanceCounter pc in _recv) pc.Dispose();
            _sent.Clear();
            _recv.Clear();
            _warm = false;
        }

        public void Dispose() { Clear(); }
    }

    // CPU 占用；计数器对象缺失或失效时自动回退/重建
    sealed class CpuCollector : IDisposable
    {
        PerformanceCounter _c;
        bool _warm;
        double _last = double.NaN;

        public double Value { get { return _last; } }

        public void Sample()
        {
            try
            {
                if (_c == null)
                {
                    try { _c = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total"); }
                    catch { _c = new PerformanceCounter("Processor", "% Processor Time", "_Total"); }
                }
                if (!_warm)
                {
                    _c.NextValue();
                    _warm = true;
                    return;
                }
                double v = _c.NextValue();
                if (v >= 0) _last = Math.Min(v, 100);
            }
            catch
            {
                if (_c != null) { _c.Dispose(); _c = null; }
                _warm = false;
            }
        }

        public void Dispose()
        {
            if (_c != null) { _c.Dispose(); _c = null; }
        }
    }

    // GPU 占用：GPU Engine 全部实例，按引擎类型分组求和后取最大（贴近任务管理器口径）。
    // GPU 进程增减会让实例列表变化，每 15 秒或出错时重建。
    sealed class GpuCollector : IDisposable
    {
        readonly List<PerformanceCounter> _counters = new List<PerformanceCounter>();
        DateTime _lastBuild = DateTime.MinValue;
        bool _warm;
        double _last = double.NaN;

        public double Value { get { return _last; } }

        public void Sample()
        {
            try
            {
                bool stale = _counters.Count == 0 ||
                    (DateTime.UtcNow - _lastBuild).TotalSeconds > 15;
                if (stale) Rebuild();
                if (_counters.Count == 0) return;

                Dictionary<string, double> sums = new Dictionary<string, double>();
                if (!_warm)
                {
                    foreach (PerformanceCounter pc in _counters) pc.NextValue();
                    _warm = true;
                    return;
                }
                for (int i = 0; i < _counters.Count; i++)
                {
                    double v;
                    try { v = _counters[i].NextValue(); }
                    catch (InvalidOperationException)
                    {
                        _lastBuild = DateTime.MinValue;   // 实例已消失，下轮重建
                        continue;
                    }
                    string t = Util.EngineType(_counters[i].InstanceName);
                    double cur;
                    sums.TryGetValue(t, out cur);
                    sums[t] = cur + v;
                }
                double max = 0;
                foreach (double v in sums.Values)
                    if (v > max) max = v;
                _last = Math.Min(max, 100);
            }
            catch
            {
                Clear();
            }
        }

        void Rebuild()
        {
            Clear();
            _lastBuild = DateTime.UtcNow;
            try
            {
                foreach (string inst in new PerformanceCounterCategory("GPU Engine").GetInstanceNames())
                {
                    try { _counters.Add(new PerformanceCounter("GPU Engine", "Utilization Percentage", inst)); }
                    catch { }
                }
            }
            catch { }   // 类别不存在（个别驱动/精简系统），一直显示 -
        }

        void Clear()
        {
            foreach (PerformanceCounter pc in _counters) pc.Dispose();
            _counters.Clear();
            _warm = false;
        }

        public void Dispose() { Clear(); }
    }

    static class Kernel32
    {
        [StructLayout(LayoutKind.Sequential)]
        public class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll")]
        public static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);
    }

    static class Util
    {
        // GPU Engine 实例名形如 pid_1234_luid_..._eng_0_engtype_3D，取最后的引擎类型
        public static string EngineType(string instance)
        {
            int i = instance.LastIndexOf("engtype_");
            return i >= 0 ? instance.Substring(i + 8) : instance;
        }

        // 图标用超短格式：↑2.3M / ↓512K / 0K；无效显示 -
        public static string FmtSpeed(double bytesPerSec)
        {
            if (double.IsNaN(bytesPerSec) || bytesPerSec < 0) return "-";
            if (bytesPerSec < 1024) return "0K";
            if (bytesPerSec < 1048576) return (bytesPerSec / 1024).ToString("0") + "K";
            return (bytesPerSec / 1048576).ToString("0.0") + "M";
        }

        // 悬停提示用较长格式
        public static string FmtLong(double bytesPerSec)
        {
            if (double.IsNaN(bytesPerSec) || bytesPerSec < 0) return "-";
            if (bytesPerSec < 1024) return bytesPerSec.ToString("0") + "B/s";
            if (bytesPerSec < 1048576) return (bytesPerSec / 1024).ToString("0.#") + "KB/s";
            return (bytesPerSec / 1048576).ToString("0.##") + "MB/s";
        }

        // 百分比：23% 或 -
        public static string Pct(double v)
        {
            if (double.IsNaN(v) || v < 0) return "-";
            if (v > 100) v = 100;
            return Math.Round(v).ToString("0") + "%";
        }

        // 百分比数字（不带 %，图标里省字符换大字号）：23 或 -
        public static string PctNum(double v)
        {
            if (double.IsNaN(v) || v < 0) return "-";
            if (v > 100) v = 100;
            return Math.Round(v).ToString("0");
        }
    }

    // ---------- 托盘界面 ----------

    sealed class TrayContext : ApplicationContext
    {
        const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        const string RunValueName = "TrayStats";

        readonly NetCollector _net = new NetCollector();
        readonly CpuCollector _cpu = new CpuCollector();
        readonly GpuCollector _gpu = new GpuCollector();

        const int SS = 4;          // 托盘 logo 渲染超采样倍数
        readonly NotifyIcon _trayIcon;
        IntPtr _hTrayIcon;         // 当前 logo 句柄，替换时销毁旧句柄防止 GDI 泄漏
        readonly int _iw, _ih;
        readonly BarForm _bar;
        readonly System.Windows.Forms.Timer _timer;

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr hIcon);

        public TrayContext()
        {
            Size sz = SystemInformation.SmallIconSize;
            _iw = Math.Max(sz.Width, 16);
            _ih = Math.Max(sz.Height, 16);

            ContextMenuStrip menu = BuildMenu();
            _bar = new BarForm(menu);

            _trayIcon = new NotifyIcon();
            _trayIcon.Text = "TrayStats";
            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.Visible = true;
            RenderIcon("T", Color.Black, "TrayStats（右键：开机自启 / 退出）");

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 1000;
            _timer.Tick += OnTick;
            _timer.Start();

            UpdateAll();   // 立即刷新一帧（速率类计数器首轮预热，显示 "-"）
            _trayIcon.ShowBalloonTip(3000, "TrayStats 已启动",
                "状态条显示在任务栏时钟左侧，右键托盘图标可设置开机自启或退出", ToolTipIcon.Info);
        }

        ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem autoRun = new ToolStripMenuItem("开机自启");
            autoRun.Checked = IsAutoRun();
            autoRun.Click += delegate
            {
                bool enable = !IsAutoRun();
                SetAutoRun(enable);
                autoRun.Checked = enable;
            };
            menu.Items.Add(autoRun);
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem exit = new ToolStripMenuItem("退出");
            exit.Click += delegate { ExitApp(); };
            menu.Items.Add(exit);
            return menu;
        }

        void OnTick(object sender, EventArgs e)
        {
            try { UpdateAll(); }
            catch { }   // 单次采样失败不影响程序继续运行
        }

        void UpdateAll()
        {
            _net.Sample();
            _cpu.Sample();
            _gpu.Sample();

            double memPct = -1, memUsed = -1, memTotal = -1;
            Kernel32.MEMORYSTATUSEX ms = new Kernel32.MEMORYSTATUSEX();
            if (Kernel32.GlobalMemoryStatusEx(ms))
            {
                memPct = ms.dwMemoryLoad;
                memTotal = ms.ullTotalPhys / 1073741824.0;
                memUsed = (ms.ullTotalPhys - ms.ullAvailPhys) / 1073741824.0;
            }

            string memTip = memPct < 0 ? "-" :
                string.Format("{0:0}%（{1:0.#}/{2:0.#}GB）", memPct, memUsed, memTotal);

            _bar.UpdateData(_net.Up, _net.Down, _cpu.Value, _gpu.Value,
                memPct, memUsed, memTotal);
            _trayIcon.Text = ClipTip("TrayStats  ↑" + Util.FmtLong(_net.Up) +
                "  ↓" + Util.FmtLong(_net.Down) +
                "  CPU " + Util.Pct(_cpu.Value) + "  GPU " + Util.Pct(_gpu.Value) +
                "  内存 " + memTip);
        }

        static string ClipTip(string tip)
        {
            return tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        // 把一行文字渲染成托盘图标（仅 logo 用一次）：先在 4 倍大画布上画字，再高质量缩小
        void RenderIcon(string text, Color color, string tip)
        {
            int W = _iw * SS, H = _ih * SS;
            using (Bitmap big = new Bitmap(W, H))
            using (Graphics g = Graphics.FromImage(big))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    sf.FormatFlags |= StringFormatFlags.NoWrap;   // 禁止折行，放不下靠字号缩小
                    using (Font font = FitFont(g, text, H * 0.92f, W - SS, sf))
                    {
                        RectangleF rect = new RectangleF(0f, 0f, W, H);
                        RectangleF shadow = new RectangleF(SS * 0.8f, SS * 0.8f, W, H);
                        using (SolidBrush back = new SolidBrush(Color.Black))
                            g.DrawString(text, font, back, shadow, sf);
                        using (SolidBrush front = new SolidBrush(color))
                            g.DrawString(text, font, front, rect, sf);
                    }
                }
                using (Bitmap small = new Bitmap(_iw, _ih))
                using (Graphics g2 = Graphics.FromImage(small))
                {
                    g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g2.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g2.CompositingQuality = CompositingQuality.HighQuality;
                    g2.DrawImage(big, new Rectangle(0, 0, _iw, _ih), 0, 0, W, H, GraphicsUnit.Pixel);
                    IntPtr h = small.GetHicon();
                    _trayIcon.Icon = Icon.FromHandle(h);
                    if (_hTrayIcon != IntPtr.Zero) DestroyIcon(_hTrayIcon);
                    _hTrayIcon = h;
                }
            }
            _trayIcon.Text = ClipTip(tip);
        }

        // 从基准字号往下缩，直到文字宽度放进图标。
        // 测量必须与绘制用同一个 StringFormat，否则画出来的字比测量值宽导致意外折行。
        Font FitFont(Graphics g, string text, float px, float maxWidth, StringFormat sf)
        {
            while (px > SS * 4f)   // 大画布下限 SS*4 = 最终 4px
            {
                using (Font test = new Font("Segoe UI", px, FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    if (g.MeasureString(text, test, 1000, sf).Width <= maxWidth) break;
                }
                px -= 2f;   // 大画布上步进 2px = 最终 0.5px
            }
            return new Font("Segoe UI", px, FontStyle.Bold, GraphicsUnit.Pixel);
        }

        static bool IsAutoRun()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath))
            {
                return k != null && k.GetValue(RunValueName) != null;
            }
        }

        static void SetAutoRun(bool enable)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (k == null) return;
                if (enable) k.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(RunValueName, false);
            }
        }

        void DisposeIcon(NotifyIcon ni, ref IntPtr hIcon)
        {
            try { ni.Visible = false; ni.Icon = null; ni.Dispose(); }
            catch { }
            if (hIcon != IntPtr.Zero) { DestroyIcon(hIcon); hIcon = IntPtr.Zero; }
        }

        void ExitApp()
        {
            _timer.Stop();
            _bar.Dispose();
            DisposeIcon(_trayIcon, ref _hTrayIcon);
            _net.Dispose();
            _cpu.Dispose();
            _gpu.Dispose();
            Application.Exit();
        }
    }

    // 任务栏状态条。Win11 任务栏是 XAML 架构且 explorer 会把 Shell_TrayWnd 压在其他
    // 置顶窗口之上（实测 SetWindowPos(HWND_TOPMOST) 无法盖过它），因此采用与
    // TrafficMonitor/ElevenClock 相同的最终方案：把本窗口 SetParent 成任务栏的子窗口。
    // 子窗口必然绘制在父窗口内容之上，且随任务栏移动/隐藏（全屏应用、自动隐藏任务栏
    // 时自动跟随消失）。背景色实时采样任务栏自身像素，视觉上与任务栏融为一体。
    sealed class BarForm : Form
    {
        double _up = double.NaN, _down = double.NaN;
        double _cpu = double.NaN, _gpu = double.NaN;
        double _memPct = -1, _memUsed = -1, _memTotal = -1;

        Font _font1, _font2;
        int _fontH;                       // 当前字体对应的状态条高度
        IntPtr _trayHwnd;
        IntPtr _attachedTo;               // 当前父窗口（explorer 重启后需重新挂载）
        Rectangle _taskbar;               // 最近一次任务栏矩形
        Rectangle _childRect;             // 状态条在任务栏客户区内的矩形
        bool _hiddenByTaskbar;

        static readonly StringFormat Sf = new StringFormat(StringFormat.GenericTypographic);
        Color _textColor = Color.Black;   // 随任务栏深浅自适应（黑字/白字）

        const int GWL_STYLE = -16;
        const int WS_POPUP = unchecked((int)0x80000000);
        const int WS_CHILD = 0x40000000;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindowW(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")]
        static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")]
        static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")]
        static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")]
        static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
        [DllImport("user32.dll")]
        static extern bool UpdateLayeredWindow(IntPtr h, IntPtr hdcDst, ref Point pptDst, ref Size psize,
            IntPtr hdcSrc, ref Point pptSrc, uint crKey, ref BLENDFUNCTION bf, uint dwFlags);
        [DllImport("user32.dll")]
        static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr h, IntPtr hdc);
        [DllImport("gdi32.dll")]
        static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")]
        static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage,
            out IntPtr bits, IntPtr hSection, uint offset);

        [StructLayout(LayoutKind.Sequential)]
        struct BLENDFUNCTION
        {
            public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        public BarForm(ContextMenuStrip menu)
        {
            ContextMenuStrip = menu;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = false;              // 变成任务栏子窗口后不再需要置顶
            DoubleBuffered = true;
            Size = new Size(180, 40);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80;          // WS_EX_TOOLWINDOW：不进 Alt+Tab
                cp.ExStyle |= 0x8000000;     // WS_EX_NOACTIVATE：点击不抢焦点
                cp.ExStyle |= 0x80000;       // WS_EX_LAYERED：分层子窗口有自己的 DWM 合成面，
                return cp;                   // 能盖过任务栏的 XAML 内容层（普通子窗口会被盖住）
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        // 挂载为任务栏子窗口（explorer 重启后自动重新挂载）
        void AttachToTaskbar()
        {
            if (_attachedTo == _trayHwnd && IsWindow(_trayHwnd)) return;
            IntPtr h = Handle;   // 确保窗口句柄已创建
            int style = GetWindowLong(h, GWL_STYLE);
            style = (style & ~WS_POPUP) | WS_CHILD;
            SetWindowLong(h, GWL_STYLE, style);
            SetParent(h, _trayHwnd);
            _attachedTo = _trayHwnd;
        }

        public void UpdateData(double up, double down, double cpu, double gpu,
            double memPct, double memUsed, double memTotal)
        {
            _up = up; _down = down; _cpu = cpu; _gpu = gpu;
            _memPct = memPct; _memUsed = memUsed; _memTotal = memTotal;
            Reposition();
        }

        void Reposition()
        {
            if (_trayHwnd == IntPtr.Zero || !IsWindow(_trayHwnd))
                _trayHwnd = FindWindowW("Shell_TrayWnd", null);

            _hiddenByTaskbar = true;
            if (_trayHwnd != IntPtr.Zero)
            {
                RECT tb;
                if (GetWindowRect(_trayHwnd, out tb))
                {
                    _taskbar = Rectangle.FromLTRB(tb.Left, tb.Top, tb.Right, tb.Bottom);
                    int tbW = _taskbar.Width, tbH = _taskbar.Height;
                    if (tbH >= 8 && tbH <= tbW)   // 正常的横向任务栏（高度<8 说明自动隐藏中）
                    {
                        int barH = tbH - 8;
                        EnsureFonts(barH);
                        AttachToTaskbar();

                        // 右边缘贴住时钟/托盘区左侧
                        IntPtr notify = FindWindowExW(_trayHwnd, IntPtr.Zero, "TrayNotifyWnd", null);
                        int rightEdge = _taskbar.Right - 320;   // 兜底：托盘区找不到时估计值
                        if (notify != IntPtr.Zero)
                        {
                            RECT nr;
                            if (GetWindowRect(notify, out nr)) rightEdge = nr.Left - 6;
                        }

                        Size wanted = MeasureSize();
                        // 挂载后坐标是任务栏客户区（即其窗口矩形原点）内的相对坐标
                        Rectangle childRect = new Rectangle(
                            rightEdge - wanted.Width - _taskbar.Left, 4, wanted.Width, barH);
                        AdaptTextColor(childRect);
                        _hiddenByTaskbar = false;
                        _childRect = childRect;
                    }
                }
            }

            if (Visible != !_hiddenByTaskbar) Visible = !_hiddenByTaskbar;
            if (Visible && _childRect.Width > 0) RenderLayered(_childRect);
        }

        // 分层窗口不走 WM_PAINT，内容用 UpdateLayeredWindow 直接以位图提交
        //（同时完成定位：pptDst 为任务栏客户区坐标）。
        // 背景完全透明，只画黑字；像素必须是预乘 alpha 的 32 位 DIB
        //（Bitmap.GetHbitmap 会丢 alpha，不能用于透明内容）。
        void RenderLayered(Rectangle childRect)
        {
            int w = childRect.Width, h = childRect.Height;
            using (Bitmap bmp = new Bitmap(w, h,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                float hf = h;
                float pad = 10;
                // GenericTypographic 排版下空格宽度为 0，间隔一律用字号的固定系数
                float gap1 = _font1.Size * 0.35f, sep1 = _font1.Size * 1.1f;
                float gap2 = _font2.Size * 0.35f, sep2 = _font2.Size * 1.0f;

                float x = pad;
                x = DrawSeg(g, "↑", _textColor, x, hf * 0.08f, _font1) + gap1;
                x = DrawSeg(g, FmtBar(_up), _textColor, x, hf * 0.08f, _font1) + sep1;
                x = DrawSeg(g, "↓", _textColor, x, hf * 0.08f, _font1) + gap1;
                DrawSeg(g, FmtBar(_down), _textColor, x, hf * 0.08f, _font1);

                float y2 = hf * 0.54f;
                float x2 = pad;
                x2 = DrawSeg(g, "CPU", _textColor, x2, y2, _font2) + gap2;
                x2 = DrawSeg(g, Util.Pct(_cpu), _textColor, x2, y2, _font2) + sep2;
                x2 = DrawSeg(g, "GPU", _textColor, x2, y2, _font2) + gap2;
                x2 = DrawSeg(g, Util.Pct(_gpu), _textColor, x2, y2, _font2) + sep2;
                x2 = DrawSeg(g, "内存", _textColor, x2, y2, _font2) + gap2;
                DrawSeg(g, _memPct < 0 ? "-" : Math.Round(_memPct).ToString("0") + "%",
                    _textColor, x2, y2, _font2);

                // 取出像素并在字节数组里完成预乘 alpha（rgb *= a / 255）
                System.Drawing.Imaging.BitmapData data = bmp.LockBits(
                    new Rectangle(0, 0, w, h),
                    System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                byte[] pixels = new byte[w * h * 4];
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                bmp.UnlockBits(data);
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    int a = pixels[i + 3];
                    if (a == 255 || a == 0) continue;
                    pixels[i] = (byte)(pixels[i] * a / 255);
                    pixels[i + 1] = (byte)(pixels[i + 1] * a / 255);
                    pixels[i + 2] = (byte)(pixels[i + 2] * a / 255);
                }

                // 创建保留 alpha 的 32 位 DIB，把预乘像素拷进去
                IntPtr screenDc = GetDC(IntPtr.Zero);
                BITMAPINFO bmi = new BITMAPINFO();
                bmi.bmiHeader.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
                bmi.bmiHeader.biWidth = w;
                bmi.bmiHeader.biHeight = -h;      // 负数 = 自上而下
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;
                bmi.bmiHeader.biCompression = 0;  // BI_RGB
                IntPtr bits;
                IntPtr hbmp = CreateDIBSection(screenDc, ref bmi, 0, out bits, IntPtr.Zero, 0);
                if (hbmp == IntPtr.Zero)
                {
                    ReleaseDC(IntPtr.Zero, screenDc);
                    return;
                }
                Marshal.Copy(pixels, 0, bits, pixels.Length);

                IntPtr memDc = CreateCompatibleDC(screenDc);
                IntPtr oldBmp = SelectObject(memDc, hbmp);
                Point dst = new Point(childRect.X, childRect.Y);
                Point src = new Point(0, 0);
                Size size = new Size(w, h);
                BLENDFUNCTION bf = new BLENDFUNCTION();
                bf.BlendOp = 0;                 // AC_SRC_OVER
                bf.BlendFlags = 0;
                bf.SourceConstantAlpha = 255;
                bf.AlphaFormat = 1;             // AC_SRC_ALPHA
                UpdateLayeredWindow(Handle, screenDc, ref dst, ref size,
                    memDc, ref src, 0, ref bf, 2 /*ULW_ALPHA*/);
                SelectObject(memDc, oldBmp);
                DeleteObject(hbmp);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        // 用最坏情况字符串定宽，避免数值变化时窗口宽度抖动（间隔用固定系数，见 RenderLayered）
        Size MeasureSize()
        {
            using (Graphics g = CreateGraphics())
            {
                float em1 = _font1.Size, em2 = _font2.Size;
                float w1 = g.MeasureString("↑", _font1, 10000, Sf).Width + em1 * 0.35f
                         + g.MeasureString("888.8MB/s", _font1, 10000, Sf).Width + em1 * 1.1f
                         + g.MeasureString("↓", _font1, 10000, Sf).Width + em1 * 0.35f
                         + g.MeasureString("888.8MB/s", _font1, 10000, Sf).Width;
                float w2 = g.MeasureString("CPU", _font2, 10000, Sf).Width + em2 * 0.35f
                         + g.MeasureString("100%", _font2, 10000, Sf).Width + em2 * 1.0f
                         + g.MeasureString("GPU", _font2, 10000, Sf).Width + em2 * 0.35f
                         + g.MeasureString("100%", _font2, 10000, Sf).Width + em2 * 1.0f
                         + g.MeasureString("内存", _font2, 10000, Sf).Width + em2 * 0.35f
                         + g.MeasureString("100%", _font2, 10000, Sf).Width;
                return new Size((int)Math.Ceiling(Math.Max(w1, w2)) + 20, _fontH);
            }
        }

        void EnsureFonts(int barH)
        {
            if (_fontH == barH && _font1 != null) return;
            if (_font1 != null) _font1.Dispose();
            if (_font2 != null) _font2.Dispose();
            _font1 = new Font("Segoe UI", Math.Max(barH * 0.40f, 9), FontStyle.Bold, GraphicsUnit.Pixel);
            _font2 = new Font("Segoe UI", Math.Max(barH * 0.34f, 8), FontStyle.Regular, GraphicsUnit.Pixel);
            _fontH = barH;
        }

        // 在状态条正下方（同一壁纸区域）的任务栏像素上取样（3 点平均），让背景色无缝融入
        // 自动深浅色适配：在状态条正下方（同壁纸列、窗口外）的任务栏像素上取 3 点平均
        // 亮度，亮度高用黑字、亮度低自动切白字；带滞回阈值，避免在临界亮度下来回闪。
        void AdaptTextColor(Rectangle childRect)
        {
            try
            {
                int y = _taskbar.Bottom - 2;
                int[] xs = {
                    childRect.X + _taskbar.Left + childRect.Width / 4,
                    childRect.X + _taskbar.Left + childRect.Width / 2,
                    childRect.X + _taskbar.Left + childRect.Width * 3 / 4
                };
                using (Bitmap bmp = new Bitmap(1, 1))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    long lum = 0;
                    for (int i = 0; i < xs.Length; i++)
                    {
                        g.CopyFromScreen(xs[i], y, 0, 0, new Size(1, 1));
                        Color c = bmp.GetPixel(0, 0);
                        lum += c.R * 299 + c.G * 587 + c.B * 114;
                    }
                    int l = (int)(lum / xs.Length / 1000);
                    if (l < 110 && _textColor != Color.White) _textColor = Color.White;
                    else if (l > 145 && _textColor != Color.Black) _textColor = Color.Black;
                }
            }
            catch { }
        }

        static string FmtBar(double bps)
        {
            if (double.IsNaN(bps) || bps < 0) return "-";
            if (bps < 1048576) return (bps / 1024).ToString("0.#") + "KB/s";
            return (bps / 1048576).ToString("0.##") + "MB/s";
        }

        float DrawSeg(Graphics g, string s, Color c, float x, float y, Font f)
        {
            using (SolidBrush b = new SolidBrush(c))
                g.DrawString(s, f, b, x, y, Sf);
            return x + g.MeasureString(s, f, 10000, Sf).Width;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_font1 != null) { _font1.Dispose(); _font1 = null; }
                if (_font2 != null) { _font2.Dispose(); _font2 = null; }
            }
            base.Dispose(disposing);
        }
    }
}
