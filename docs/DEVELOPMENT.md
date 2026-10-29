# 开发说明

## 环境与入口

使用 Visual Studio 打开根目录 `TunHotspot.slnx`，项目文件在 `src/TunHotspot.WinUI/TunHotspot.WinUI.csproj`。平台为 x64，发布采用 Release / win-x64。目标框架为 `net10.0-windows10.0.26100.0`。

本次实际构建使用系统 `.NET SDK 11.0.100-rc.3.26522.148`，发布应用使用 .NET 10.0.14 运行库。SDK 的预览版本提示不表示发布应用面向 .NET 11。后续可用稳定 .NET 10 SDK 构建。

`build.ps1` 会限定当前进程的 SDK 环境并在完成后恢复，不修改系统环境变量。它关闭 MSBuild workload resolver 以适配当前本机 SDK 组件安装状态。`-Offline` 仅使用 `.tools/winui-build-tools/feed`；在线构建使用该缓存和 nuget.org。

## 代码职责

| 文件 | 职责 |
| --- | --- |
| `App.xaml.cs` | WinUI 初始化、AppInstance 单实例、激活窗口、安装器运行检测 mutex |
| `MainWindow.xaml` | 原生中文 UI、状态卡片、开关按钮、错误提示 |
| `MainWindow.xaml.cs` | 3 秒只读刷新、用户操作协调、窗口 ICO、关闭时保持网络状态 |
| `HotspotService.cs` | 匹配 Kumiryo 网卡与 ConnectionProfile，调用热点 API |
| `AppLog.cs` | 本地操作元数据和当前状态记录 |
| `Assets/` | 原创图标及 SVG 源文件 |

## 网络行为

按名称 `Kumiryo` 查找状态为 Up 的网卡，再按 GUID 匹配 Windows ConnectionProfile。优先使用 InternetAccess 的配置，调用 `NetworkOperatorTetheringManager.CreateFromConnectionProfile` 创建管理器。网卡 GUID 发生变化时重建管理器。

`StartTetheringAsync` 和 `StopTetheringAsync` 仅通过用户按钮触发。启动、窗口关闭、定时器和安装器均不调用网络开关。没有后台服务、计划任务或自启项。窗口关闭只停止 UI 定时器；热点由 Windows 保持。

启动中仍保留停止按钮，停止操作会取消待完成的启动请求并调用 Windows 停止接口。操作版本号防止旧请求完成后覆盖较新的 UI。状态读取异常会保留恢复入口。没有自动重试开关网络的逻辑。

## 发布与资源

采用 unpackaged/self-contained 目录发布，包含 .NET、WinUI 及传递依赖。`EnableMsixTooling=true` 用于生成发布所需的 `TunHotspot.pri`；移除此项可能导致启动时 XAML 解析失败。

不要把项目的 `.tools`、`bin`、`obj`、诊断日志或网络订阅备份加入软件安装包。安装包仅收录 `artifacts/win-x64` 的运行文件和用户说明，排除 PDB。

图标源文件为 `Assets/TunHotspot.svg`；`tools/Generate-Icon.ps1` 使用同一几何参数在 Windows GDI+ 中重绘并生成 16、20、24、32、40、48、64、128、256 像素的 ICO 层。修改 SVG 外观时需同步生成脚本参数。

## 安装包

`tools/Initialize-InstallerTools.ps1` 从官方 GitHub 发布下载 Inno Setup 7.1.0，要求 Authenticode 签名有效且发布者为 Pyrsys B.V.。以便携模式解包到 `.tools`，不注册开发工具或文件关联。

`installer/TunHotspot.iss` 使用当前用户安装，不要求管理员权限。版本来自 csproj。更新和卸载通过 `TunHotspot.WinUI.Running` mutex 检测助手是否运行，只提示手动关闭；不使用 Restart Manager 自动终止应用。卸载保留 `%LOCALAPPDATA%/TunHotspot.WinUI` 中的操作记录。

构建安装包：

```powershell
.\tools\Build-Installer.ps1 -Publish -Offline
```

`tools/Test-Installer.ps1` 可在助手已关闭、电脑尚未安装正式版本时执行安装/卸载往返验证。它拒绝覆盖已安装版本；临时文件和结果保存在 `artifacts/qa`，不启动软件或开关网络。

官方参考：[热点 API](https://learn.microsoft.com/en-us/uwp/api/windows.networking.networkoperators.networkoperatortetheringmanager)、[WinUI 无打包部署](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app)、[Inno 便携实现](https://github.com/jrsoftware/issrc/blob/main/isportable.iss)、[安装器应用关闭策略](https://jrsoftware.org/ishelp/topic_setup_closeapplications.htm)。
