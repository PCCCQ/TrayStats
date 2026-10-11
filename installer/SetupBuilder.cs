// SetupBuilder.cs — TrayStats 安装器生成器 + 安装器本体（一个文件两种用途）
//
//   SetupBuilder.exe /make <输出Setup.exe> <TrayStats.exe> <版本号>
//       把自身字节 + 载荷（应用文件、版本号）拼接，生成自包含的 Setup.exe
//   Setup.exe            双击运行，弹出安装界面（安装到当前用户目录，无需管理员权限）
//   Setup.exe /silent    静默安装（默认开机自启），供自动化测试 / 企业部署使用
//   Setup.exe /uninstall [/silent]   卸载：删除文件、快捷方式与开机自启
//
// 载荷格式：[自身字节]["TSETUP01"][条目数][每条目: 名称长度|名称|数据长度|数据]
// 运行时从自身文件尾部定位标记并解析，因此单个 Setup.exe 即自包含安装包。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

// 载荷中的一个文件条目
sealed class Entry
{
    public string Name;
    public byte[] Data;
}

static class Program
{
    const string Marker = "TSETUP01";
    const string AppExeName = "TrayStats.exe";
    const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";

    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    [STAThread]
    static int Main(string[] args)
    {
        try { SetProcessDPIAware(); } catch { }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (args.Length >= 4 && args[0] == "/make")
            return Make(Application.ExecutablePath, args[1], args[2], args[3]);

        bool uninstall = args.Length >= 1 && args[0] == "/uninstall";
        bool silent = Array.IndexOf(args, "/silent") >= 0;

        Entry[] payload = ParsePayload(File.ReadAllBytes(Application.ExecutablePath));
        if (payload == null)
        {
            MessageBox.Show("缺少安装载荷：请使用 package.ps1 生成 Setup.exe，不要直接运行 SetupBuilder.exe。",
                "TrayStats", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        string version = "";
        byte[] ver = FindEntry(payload, "version.txt");
        if (ver != null) version = Encoding.UTF8.GetString(ver).Trim();

        if (uninstall) return DoUninstall(silent);
        if (silent) { DoInstall(payload, true); return 0; }
        Application.Run(new SetupForm(payload, version));
        return 0;
    }

    // ---------- 载荷 ----------

    static Entry MakeEntry(string name, byte[] data)
    {
        Entry e = new Entry();
        e.Name = name;
        e.Data = data;
        return e;
    }

    static void WriteUInt32(MemoryStream ms, uint v)
    {
        ms.WriteByte((byte)v);
        ms.WriteByte((byte)(v >> 8));
        ms.WriteByte((byte)(v >> 16));
        ms.WriteByte((byte)(v >> 24));
    }

    static void WriteUInt64(MemoryStream ms, ulong v)
    {
        for (int i = 0; i < 8; i++) ms.WriteByte((byte)(v >> (8 * i)));
    }

    static uint ReadUInt32(byte[] b, ref int off)
    {
        uint v = (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));
        off += 4;
        return v;
    }

    static ulong ReadUInt64(byte[] b, ref int off)
    {
        ulong v = 0;
        for (int i = 7; i >= 0; i--) v = (v << 8) | b[off + i];
        off += 8;
        return v;
    }

    // /make：自身字节 + 标记 + 载荷 → Setup.exe
    static int Make(string selfPath, string output, string appExe, string version)
    {
        Entry[] entries = new Entry[] {
            MakeEntry(AppExeName, File.ReadAllBytes(appExe)),
            MakeEntry("version.txt", Encoding.UTF8.GetBytes(version))
        };
        using (MemoryStream ms = new MemoryStream())
        {
            byte[] self = File.ReadAllBytes(selfPath);
            ms.Write(self, 0, self.Length);
            byte[] m = Encoding.ASCII.GetBytes(Marker);
            ms.Write(m, 0, m.Length);
            WriteUInt32(ms, (uint)entries.Length);
            foreach (Entry e in entries)
            {
                byte[] name = Encoding.UTF8.GetBytes(e.Name);
                WriteUInt32(ms, (uint)name.Length);
                ms.Write(name, 0, name.Length);
                WriteUInt64(ms, (ulong)e.Data.LongLength);
                ms.Write(e.Data, 0, e.Data.Length);
            }
            File.WriteAllBytes(output, ms.ToArray());
        }
        return 0;
    }

    static Entry[] ParsePayload(byte[] self)
    {
        try
        {
            byte[] m = Encoding.ASCII.GetBytes(Marker);
            int pos = -1;
            for (int i = self.Length - m.Length; i >= 0; i--)
            {
                bool ok = true;
                for (int j = 0; j < m.Length; j++)
                {
                    if (self[i + j] != m[j]) { ok = false; break; }
                }
                if (ok) { pos = i + m.Length; break; }
            }
            if (pos < 0) return null;

            List<Entry> list = new List<Entry>();
            int off = pos;
            uint count = ReadUInt32(self, ref off);
            for (uint i = 0; i < count; i++)
            {
                int nameLen = (int)ReadUInt32(self, ref off);
                if (nameLen < 0 || nameLen > 1024 || off + nameLen > self.Length) return null;
                string name = Encoding.UTF8.GetString(self, off, nameLen);
                off += nameLen;
                long len = (long)ReadUInt64(self, ref off);
                if (len < 0 || off + len > self.Length) return null;
                byte[] data = new byte[len];
                Array.Copy(self, off, data, 0, len);
                off += (int)len;
                list.Add(MakeEntry(name, data));
            }
            return list.ToArray();
        }
        catch
        {
            return null;
        }
    }

    static byte[] FindEntry(Entry[] payload, string name)
    {
        foreach (Entry e in payload)
            if (e.Name == name) return e.Data;
        return null;
    }

    // ---------- 安装 / 卸载 ----------

    static string TargetDir()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "TrayStats");
    }

    static void KillRunning()
    {
        foreach (Process p in Process.GetProcessesByName("TrayStats"))
        {
            try { p.Kill(); p.WaitForExit(3000); } catch { }
        }
    }

    // 用 PowerShell 的 WScript.Shell 创建 .lnk（系统自带，无需 COM 互操作声明）
    static void MakeShortcut(string lnkPath, string targetPath, string arguments, string description)
    {
        string ps = "$ws = New-Object -ComObject WScript.Shell; " +
            "$l = $ws.CreateShortcut('" + lnkPath + "'); " +
            "$l.TargetPath = '" + targetPath + "'; " +
            (arguments.Length > 0 ? "$l.Arguments = '" + arguments + "'; " : "") +
            "$l.WorkingDirectory = '" + Path.GetDirectoryName(targetPath) + "'; " +
            "$l.Description = '" + description + "'; " +
            "$l.Save()";
        ProcessStartInfo psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -Command \"" + ps + "\"");
        psi.CreateNoWindow = true;
        psi.UseShellExecute = false;
        Process p = Process.Start(psi);
        if (p != null) p.WaitForExit(10000);
    }

    static void StartMenuShortcut(string name, string targetPath, string arguments, string description)
    {
        string sm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
        MakeShortcut(Path.Combine(sm, name), targetPath, arguments, description);
    }

    internal static void DoInstall(Entry[] payload, bool autostart)
    {
        KillRunning();
        string target = TargetDir();
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, AppExeName), FindEntry(payload, AppExeName));
        File.WriteAllBytes(Path.Combine(target, "Setup.exe"), File.ReadAllBytes(Application.ExecutablePath));

        StartMenuShortcut("TrayStats.lnk", Path.Combine(target, AppExeName), "", "TrayStats - taskbar live stats");
        StartMenuShortcut("Uninstall TrayStats.lnk", Path.Combine(target, "Setup.exe"), "/uninstall",
            "Uninstall TrayStats");

        if (autostart)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                k.SetValue("TrayStats", "\"" + Path.Combine(target, AppExeName) + "\"");
            }
        }

        Process.Start(new ProcessStartInfo(Path.Combine(target, AppExeName)) { UseShellExecute = true });
    }

    static int DoUninstall(bool silent)
    {
        if (!silent &&
            MessageBox.Show("确定卸载 TrayStats 吗？", "TrayStats 卸载",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return 0;

        KillRunning();
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
        {
            if (k != null) k.DeleteValue("TrayStats", false);
        }
        string sm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
        try { File.Delete(Path.Combine(sm, "TrayStats.lnk")); } catch { }
        try { File.Delete(Path.Combine(sm, "Uninstall TrayStats.lnk")); } catch { }

        // 自删除：把自身复制到临时目录，由 cmd 延迟删除整个安装目录
        string target = TargetDir();
        string tmp = Path.Combine(Path.GetTempPath(), "TrayStats-uninst.exe");
        File.Copy(Application.ExecutablePath, tmp, true);
        ProcessStartInfo psi = new ProcessStartInfo("cmd.exe",
            "/c timeout /t 2 /nobreak >nul & rd /s /q \"" + target + "\" & del /f /q \"" + tmp + "\"");
        psi.CreateNoWindow = true;
        psi.UseShellExecute = false;
        Process.Start(psi);
        return 0;
    }
}

// 安装向导界面
sealed class SetupForm : Form
{
    readonly Entry[] _payload;
    CheckBox _auto;
    Button _btn;
    Label _status;
    bool _installed;

    public SetupForm(Entry[] payload, string version)
    {
        _payload = payload;
        Text = version.Length > 0 ? "TrayStats " + version + " 安装程序" : "TrayStats 安装程序";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(440, 180);

        Label title = new Label();
        title.Text = version.Length > 0 ? "TrayStats " + version : "TrayStats";
        title.Font = new Font("Segoe UI", 14, FontStyle.Bold);
        title.Location = new Point(18, 14);
        title.AutoSize = true;
        Controls.Add(title);

        Label desc = new Label();
        desc.Text = "任务栏实时网速 / CPU / GPU / 内存状态条\n安装到当前用户目录（无需管理员权限）";
        desc.Location = new Point(20, 52);
        desc.AutoSize = true;
        Controls.Add(desc);

        _auto = new CheckBox();
        _auto.Text = "开机自动运行（推荐）";
        _auto.Checked = true;
        _auto.Location = new Point(20, 100);
        _auto.AutoSize = true;
        Controls.Add(_auto);

        _btn = new Button();
        _btn.Text = "开始安装";
        _btn.Location = new Point(20, 132);
        _btn.Size = new Size(110, 30);
        _btn.Click += OnInstall;
        Controls.Add(_btn);

        _status = new Label();
        _status.Location = new Point(145, 140);
        _status.AutoSize = true;
        Controls.Add(_status);
    }

    void OnInstall(object sender, EventArgs e)
    {
        if (_installed) { Close(); return; }
        _btn.Enabled = false;
        _status.Text = "正在安装...";
        Application.DoEvents();
        try
        {
            Program.DoInstall(_payload, _auto.Checked);
            _status.Text = "安装完成，TrayStats 已启动！";
            _btn.Text = "完成";
            _btn.Enabled = true;
            _installed = true;
        }
        catch (Exception ex)
        {
            _status.Text = "安装失败：" + ex.Message;
            _btn.Enabled = true;
        }
    }
}
