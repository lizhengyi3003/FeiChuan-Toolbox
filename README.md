# TUN 热点与启动工具箱

WinUI 3 / .NET 10 的 Windows 11 x64 工具箱，包含原有 Kumiryo TUN 热点、启动入口扫描、保守分类、持续观察、状态偏离提醒和 Codex 待分析区。当前版本 **1.1.0，只读监测版**。

## 使用与分发

- 安装包：`artifacts/installer/TunHotspot-1.1.0-Setup-x64.exe`
- 便携包：`artifacts/distribution/TunHotspot-1.1.0-win11-x64.zip`
- 直接运行：`artifacts/toolbox-preview/TunHotspot.exe`
- [使用说明](docs/使用说明.txt)
- [采集能力与验证记录](docs/启动管理-能力与验证.md)

便携版须保留整个目录。包含 .NET、WinUI、app-local VC++ 运行文件；使用者不需要 SDK、Visual Studio、开发机路径、Codex 或 API 密钥。包内不携带本机扫描结果与用户数据库。首次复制到另一台电脑后建立独立本地数据。安装器为当前用户安装，默认无需管理员。

已在断网、没有 .NET SDK/Codex 的干净 Windows 11 26100 沙盒验证复制到带空格的目录后运行。其他 Windows 11 构建和 ARM64 尚未逐一验证。程序与安装器未签名，提供 SHA-256。原始 1.0.1 发布目录 `artifacts/win-x64` 保留。

## 扫描与持续监测

打开后先建立普通权限基线；顶部全局扫描通过 UAC 单次采集，或调用已安装的 LocalSystem 机器组件。显示真实阶段进度、取消状态和覆盖缺口；无法重新确认的旧入口保留并标记，不推定已删除。

采集 Run/RunOnce/StartupApproved、启动文件夹、可访问账户、打包应用启动、服务/驱动、计划任务 XML/COM 动作、Winlogon/Session Manager/LSA/AppInit/IFEO 等间接入口、批准的 Shell 扩展、打印/网络提供程序、WMI 永久订阅和当前进程。详见能力表。登记不等于常驻，父 PID 不等于确定启动原因。

默认每 5 分钟补扫；注册表连续订阅、5 秒状态采样、目录变化和任务 ETW 提供提前核对线索，并防抖。机器组件采集 ETW 进程创建/退出，记录事件丢失和持久化故障。普通模式明确说明权限范围。

在其他软件中设好状态后，重新扫描并点击“监视当前状态”。偏离先写持久告警，再提交 Windows 通知。可以搜索历史、标记已读、重发及重新关注，恢复后结案，再次偏离形成新事件。不自动争抢写入，不推定修改者，也不保证绕过系统或厂商保护。设置和任务管理器可能操作同一配置。

## 分类与 Codex 分析

建议分为建议保留、按需保留、可考虑取消、需修复、待分析。规则带版本且属于启发式；签名未知不等于恶意。未知项目自动进入待分析区，用户可选择条目和记录用途。

待分析页可注册本机 STDIO MCP，然后在 Codex 中请求分析。也可导出带预览的脱敏 JSON、导入结果。单批最多 100 项，校验本机、批次、当前配置/文件/用途指纹和字段，只更新建议库，不执行模型返回的命令。不读取聊天数据库或账号登录凭据。

本地桥接工具：`startup_list_pending`、`startup_get_analysis_bundle`、`startup_get_evidence`、`startup_submit_analysis`。不提供任意文件/脚本/SQL/系统修改接口。连接方法参考 [OpenAI 官方文档](https://learn.chatgpt.com/docs/extend/mcp?surface=cli)。

手动注册时，将下面路径换为实际安装路径：

```powershell
codex mcp add TunHotspotStartup -- 'D:\MyTools\TunHotspot\Engine\TunHotspot.Engine.exe' --mcp
```

按客户端提示刷新连接或开始后续对话。连接成功不等于已启动分析。移动目录后重新注册；移除连接用 `codex mcp remove TunHotspotStartup`。AI 可选，不另设 API 调用或密钥要求。

## 外观、托盘和热点

支持 Acrylic、Mica、MicaAlt、None、主题、DirectWrite 字体枚举/刷新和字号，不实现 Aero。系统透明策略与高对比度优先。关闭窗口默认收起到托盘，可扫描、打开通知、暂停和退出。保存窗口位置，并修正到可见工作区。

软件自身界面统一使用 WinUI 3，包括独立托盘弹出窗口、按钮、外观、对话框与进度展示。托盘菜单跟随主题/字体/材质，支持失焦收起、Esc、上下键和工作区定位；不使用 WinForms、WPF 或传统 Win32 菜单。Windows 托盘图标、系统授权、系统通知和系统文件选择仍调用相应的 Windows 接口。

热点仅由“开启兼容热点/关闭热点”按钮改变；启动、扫描、退出不会开关热点或改 Kumiryo、DNS、防火墙、分流。新电脑的网卡/TUN 仍需核对。

## 数据、组件和卸载

用户数据库在 `%LOCALAPPDATA%\TunHotspot.Toolbox`，操作日志在 `%LOCALAPPDATA%\TunHotspot.WinUI`。SQLite WAL/事务；普通事件默认 7 天、快照 30 天，事件逻辑容量超过 450 MiB 时清理最旧一部分。告警和建议历史独立保存，物理空间回收取决于 SQLite；不保证磁盘写满时仍持久保存。

机器组件按用户操作经 UAC 安装到受控 `%ProgramFiles%\TunHotspot\Monitor`，机器数据在受控 `%ProgramData%\TunHotspot\Monitor`。服务名 `TunHotspotStartupMonitor`，当前用户登录入口 `TunHotspot.Toolbox.Agent`。使用版本目录、复制校验、数据库备份、失败服务路径回退；拒绝用户预占的受控目录和重解析点。

UI/通知继续以普通权限运行。机器组件只接受核实账户的有限 IPC。退出托盘不卸载服务；暂停状态单独保存。卸载组件删除自身服务和登录入口，保留历史及受控版本以供恢复。移动便携目录后重新安装/更新组件，以刷新托盘启动路径。

安装器更新/卸载前要求用户退出托盘，不终止其他软件。有组件时卸载先提示 UAC；静默卸载须先移除组件。当前版本不禁用、删除其他软件入口。

## 构建与检查

开发环境需要 Windows x64、.NET 10 或更高 SDK、Windows SDK 26100、WinUI 构建工具。核心依赖为 WindowsAppSDK.WinUI 2.6.4、.NET 10.0.14、TraceEvent 4.0.0。当前机器 SDK 11 RC 的构建提示不改变 .NET 10 自包含目标。

```powershell
.\build.ps1 -Publish
.\build.ps1 -Publish -Offline # 需要已准备的 NuGet 缓存/本地 feed
.\tools\Build-Installer.ps1
# 测试项目需要 MSBuildEnableWorkloadResolver=false（当前预览 SDK 环境）
dotnet run --project src\TunHotspot.Tests -c Release
```

默认输出 `artifacts/toolbox-preview`，不覆盖 1.0.1。`Prepare-Distribution.ps1` 收集签名有效的官方 VC/Redist、依赖许可证和校验表。安装器使用 Inno Setup 7，可运行 `tools/Initialize-InstallerTools.ps1` 准备。

`tools/Test-WindowsSandbox.ps1` 使用已有 Windows Sandbox，关闭网络/剪贴板共享，只读映射发布文件。组件测试脚本拒绝在宿主机执行。XAML 截图用于布局验证，不代表原生材质的视觉验收。完整重启轨迹、通知点击唤回与不同硬件仍需后续实际验证。

## 源码

- `TunHotspot.Core`：只读采集、分类、监测、SQLite、模型与 IPC。
- `TunHotspot.Engine`：UAC 代理、机器组件、MCP。
- `TunHotspot.WinUI`：普通权限界面、托盘、通知与热点。
- `TunHotspot.Tests`：状态、持久化、边界和协议验证。
