# 开发说明

当前产品为热点与启动工具箱。模块边界见 [ARCHITECTURE.md](ARCHITECTURE.md)，功能范围见 [README](../README.md)，验收入口见 [VALIDATION.md](VALIDATION.md)。历史证据按日期和版本标注。

## 构建配置

使用 Visual Studio 打开 TunHotspot.slnx。四个项目继承根目录 Directory.Build.props 的产品版本、目标框架、可空检查及隐式 using；平台 Windows x64。不要在 csproj、MCP、服务或安装器另写产品版本。协议、数据库和分类规则版本独立维护。

目标框架 net10.0-windows10.0.26100.0，UI 最低声明 Windows 11 22000；干净系统实测为 26100，不代表全部构建与硬件已验收。构建需要 .NET 10 或更高 SDK、Windows SDK 26100、WinUI 工具和 NuGet 依赖。本机使用 SDK 11 RC，自包含应用使用 .NET 10，实际依赖以 runtimeconfig、deps 和 Notices/dependencies.json 为准。

build.ps1 只为当前进程配置 SDK 环境并恢复，关闭本机预览 SDK 的 workload resolver，不改系统环境。-Offline 使用预先准备的本地 feed。

```powershell
.\build.ps1
.\build.ps1 -Publish # 默认 artifacts/toolbox-preview
$env:MSBuildEnableWorkloadResolver='false'
dotnet run --project src\TunHotspot.Tests -c Release
```

## 正式分发

```powershell
.\tools\Build-Release.ps1
# 离线依赖齐全时可加 -Offline；默认使用新空目录。
# 指定 -OutputDirectory 时目录也必须为空。
```

从同一发布目录生成安装包和便携 ZIP，核对产品版本、使用说明、更新记录、全文件 SHA-256 和私有文件排除规则。输出在 artifacts/installer、artifacts/distribution，生成各包 SHA-256 及 release-版本.json。包内 release.json 记录版本、提交、框架、架构和构建时间；files.sha256 校验除自身外的发布文件。正式交付前先提交本次源码与说明；提交号本身不能证明工作区没有后续改动。

Prepare-Distribution.ps1 收集 .NET/WinUI 依赖许可证、Microsoft 签名有效的 app-local VC++ 运行文件、使用说明和更新记录，排除 PDB。不得打包 .tools、源码、数据库、分析 JSON、审计和本机日志。发布为 unpackaged/self-contained；EnableMsixTooling 用于生成 PRI，不能未经运行验证移除。

安装器使用 Inno Setup 7，tools/Initialize-InstallerTools.ps1 校验官方签名后准备便携编译器。单独重建安装器用 tools/Build-Installer.ps1 -PublishDirectory <完整发布目录>，拒绝 UI、Engine、Core 版本不同的目录。安装脚本不另提供产品版本默认值。

安装为当前用户，默认不请求管理员。更新和卸载要求退出托盘，通过 mutex 检测，不强制终止应用。若有机器组件，卸载另行 UAC 移除自身组件，保留历史。系统控制和安装测试优先在断网 Windows Sandbox 内运行。

## 维护约定

- HotspotPage.xaml/.cs 与 HotspotPage 类名对应；工具箱主窗口是 ToolboxWindow。热点仅在用户点击按钮时开关。
- 源码在 src，构建/隔离测试在 tools，安装定义在 installer，说明在 docs。生成文件、历史发布、本机证据在被 Git 忽略的 artifacts；保留用户提供的 docs/images。
- 改动扫描、模型、指纹、存储和权限时跑功能检查；改分发时核对完整文件并做干净系统运行。仅文档变化无需重复全部控制测试。
- 不把旧快照、取消授权、部分成功或界面截图写成完整管理员扫描、真实重启轨迹或原生材质验收。
