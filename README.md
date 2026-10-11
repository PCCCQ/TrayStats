# TrayStats — Windows 11 任务栏实时状态条

在 Windows 11 任务栏上（紧挨时钟/托盘图标左侧）嵌入一条实时状态栏，字号与时钟一致，
透明背景、文字颜色随任务栏深浅自动适配：

```
任务栏：  [开始] ...    ↑ 18.5KB/s  ↓ 4.4KB/s    ^ ...   13:45
                        CPU 36%  GPU 6%  内存 48%          2026/10/11
```

- **第一行**：↑ 上行网速 / ↓ 下行网速，KB/s、MB/s 自动换算
- **第二行**：CPU / GPU / 内存占用百分比
- 透明背景 + 自适应文字颜色（每秒采样任务栏亮度，深色自动切白字）
- 数值每秒刷新；全屏应用、任务栏自动隐藏时随任务栏一起消失，不遮挡内容
- 托盘区保留 1 个 "T" 图标：右键菜单可**开机自启**、**退出**

单文件 C# 程序，用 **Windows 系统自带的 .NET Framework 编译器**构建，
不依赖任何运行库或 SDK，普通权限运行，常驻内存约 50MB。

## 安装

到 [Releases](../../releases) 页面下载，两种包任选：

| 包 | 适合 | 用法 |
|---|---|---|
| `TrayStats-x.y.z-Setup.exe` | 想装好就不管了 | 双击 → 自动安装到当前用户目录、创建开始菜单快捷方式、设置开机自启并启动 |
| `TrayStats-x.y.z-portable.zip` | 绿色便携党 | 解压后直接双击 `TrayStats.exe` |

卸载：开始菜单 → "Uninstall TrayStats"，或在托盘图标右键退出后手动删除文件、
删除注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 下的 `TrayStats` 值。

安装器细节：安装到 `%LocalAppData%\Programs\TrayStats`（仅当前用户，无需管理员权限），
Setup.exe 由 Windows 自带的 iexpress 制作，安装脚本见 [installer/](installer/)。

## 从源码构建

要求：Windows 10/11（自带 .NET Framework 4.8 与编译器），无需安装任何 SDK。

```powershell
pwsh -File build.ps1        # 编译 → build\TrayStats.exe
pwsh -File package.ps1      # 编译 + 打包 → dist\ 便携版 zip 与 Setup.exe
TrayStats.exe test          # 无界面自检（验证本机计数器可用，输出 TrayStats_selftest.log）
```

## 项目结构

```
├── src/TrayStats.cs        全部源码（单文件，C# 5 语法，兼容系统自带编译器）
├── installer/              安装包的组成脚本（install.cmd / uninstall.cmd / shortcut.ps1）
├── build.ps1               编译脚本（调用系统自带 csc.exe）
├── package.ps1             打包脚本（便携版 zip + iexpress Setup.exe）
├── .gitignore / LICENSE    MIT 许可证
```

## 实现说明（Win11 任务栏的三道锁）

Win11 任务栏是 XAML 架构，Win10 时代 TrafficMonitor 的窗口嵌入法已失效。本程序的做法：

1. 把状态条窗口 `SetParent` 为任务栏 `Shell_TrayWnd` 的**子窗口**（随任务栏移动/隐藏）；
2. 加 `WS_EX_LAYERED` 分层样式——普通子窗口会被任务栏的 XAML 合成层盖住，
   分层子窗口有自己的 DWM 合成面，能显示在 XAML 之上；
3. 内容不走 WM_PAINT，而是每秒渲染成 32 位 ARGB 位图（透明底、预乘 alpha），
   经 `CreateDIBSection` + `UpdateLayeredWindow` 提交（同时完成定位）。

## 数据口径

- **网速**：全部网卡 Bytes Sent/Received per sec 求和（与任务管理器一致）
- **CPU**：`Processor Information(_Total)\% Processor Utility`（任务管理器同源），
  不支持时回退 `% Processor Time`
- **GPU**：`GPU Engine` 计数器按引擎类型（3D/Copy/视频编解码…）分组求和取最大，
  AMD/NVIDIA 通用，与任务管理器口径接近
- **内存**：物理内存使用率
- 全部通过 .NET `PerformanceCounter` 读取（内部按计数器索引解析，不受系统语言影响），
  无需管理员权限

## 已知限制

- 仅支持横向任务栏（竖直任务栏下状态条自动隐藏），多显示器时显示在主任务栏
- 状态条位置在时钟左侧，暂不可自定义

## License

[MIT](LICENSE)
