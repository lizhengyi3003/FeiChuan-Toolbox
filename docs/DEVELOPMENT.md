# 开发说明

当前产品为“飞船的小工具箱”。模块边界见 [ARCHITECTURE.md](ARCHITECTURE.md)，文件归属见 [工程目录](PROJECT-STRUCTURE.md)，功能范围见 [README](../README.md)，验收入口见 [VALIDATION.md](VALIDATION.md)。历史证据按日期和版本标注。

## 构建配置

使用 Visual Studio 打开 TunHotspot.slnx。解决方案分别显示三个产品工程、功能检查和两个隔离夹具工程。产品工程和功能检查继承 Directory.Build.props 的产品版本、目标框架、可空检查及隐式 using；平台 Windows x64。NuGet 依赖版本统一放在 Directory.Packages.props，csproj 只声明依赖名称。模拟客户端的版本由夹具单独声明，不属于产品版本。协议、数据库和分类规则版本独立维护。

目标框架 net10.0-windows10.0.26100.0，UI 最低声明 Windows 11 22000；干净系统实测为 26100，不代表全部构建与硬件已验收。工程脚本使用 PowerShell 7，构建固定使用 global.json 指定的 .NET 10 稳定版 SDK、Windows SDK 26100、WinUI/C++ 工具和 NuGet 依赖。依赖按六个工程的 packages.lock.json 锁定，默认仅使用公共 NuGet；实际运行依赖以 runtimeconfig、deps 和 Notices/dependencies.json 为准。软件使用者不需要这些工程工具。

build.ps1 只为当前进程配置 SDK 环境并恢复，关闭本机预览 SDK 的 workload resolver，不改系统环境。-Offline 使用预先准备的本地 feed。

```powershell
.\build.ps1
.\build.ps1 -Offline
.\test.ps1 -Offline
.\build.ps1 -Publish -Offline -OutputDirectory artifacts\temp\my-review
# 不指定输出时，开发发布默认 artifacts/temp/preview/app
```

`test.ps1` 先执行工程检查（解决方案/项目/资源路径、中央依赖、PowerShell 语法和文档链接），再运行原有控制台功能检查；`-NoRestore` 只适合已还原依赖的工作区。构建与测试共享 tools/common/DotNet.ps1，SDK 环境在结束时恢复。

## 正式分发

```powershell
.\tools\build\Build-Release.ps1
.\tools\build\Build-Release.ps1 -Offline -WorkingTree # 本地未提交构建，如实标记
.\tools\test\Test-Release.ps1
```

完整打包先在 artifacts/temp 的唯一目录生成 app 和 packages，校验程序集、手册、全文件清单、安装包和便携 ZIP 内每个文件后，才切换到 artifacts/current。current 下 release.json 记录版本、提交、未提交标记及包 SHA-256；app/release.json 记录运行构建身份。已有 current 无进程占用且元数据有效时移到 archive/releases；切换失败尽力还原原目录。单纯编译失败不会替换 current。默认要求源码已提交；-WorkingTree 只用于明确的本地未提交构建，不自动创建提交。

Prepare-Distribution.ps1 收集 .NET/WinUI 依赖许可证、Microsoft 签名有效的 app-local VC++ 运行文件、使用说明和更新记录，排除 PDB。不得打包 .tools、源码、数据库、分析 JSON、审计和本机日志。发布为 unpackaged/self-contained；EnableMsixTooling 用于生成 PRI，不能未经运行验证移除。

安装器使用 Inno Setup 7，tools/build/Initialize-InstallerTools.ps1 校验官方签名后准备便携编译器。单独重建安装器用 tools/build/Build-Installer.ps1 -PublishDirectory <完整发布目录>，拒绝 UI、Engine、Core 版本不同的目录。单独编译默认输出 artifacts/temp/preview/packages，也可用 -OutputDirectory 指向独立验证目录；完整发布才进入 current/packages。安装脚本不另提供产品版本默认值。

安装为当前用户，按目标目录权限决定是否请求当前账户管理员。升级协调器仅处理本安装目录下经身份核验的本软件进程，显式授权后允许有限退出兜底；外部占用会阻止替换。若有机器组件，卸载另行 UAC 移除自身组件，保留历史。系统控制和安装测试优先在断网 Windows Sandbox 内运行。

## 界面和隔离验证

```powershell
.\tools\test\ui\Test-ReliabilityUi.ps1 -ApplicationPath '<本次发布目录>\TunHotspot.exe' -OutputPath '<新结果路径>.json'
.\tools\test\ui\Test-ConfigurationRecoveryUi.ps1 -ApplicationPath '<本次发布目录>\TunHotspot.exe' -OutputPath '<新结果路径>.json'
.\tools\test\ui\Test-TrayUi.ps1 -ApplicationPath '<本次发布目录>\TunHotspot.exe' -OutputPath '<新结果路径>.json' -ScreenshotPath '<新截图路径>.png'
.\tools\test\sandbox\Test-WindowsSandbox.ps1 -PublishDirectory '<本次发布目录>'
```

UI 脚本创建自己的 QA 实例及临时数据，测试时不要让多个 UI Automation 脚本争用前台窗口。Kumiryo 集成夹具可从 tests/Fixtures 下的项目发布到独立 artifacts/reports/tests 目录，再传给 Test-KumiryoSandbox.ps1；只在专用沙盒中运行 Guest 脚本。宿主安装器测试在 tools/test/local，检测到正式安装会拒绝覆盖。

## 维护约定

安装器的原生提权在 installer/Elevation.iss；只读最小检查 tools/test/local/UacHandoffProbe.iss 引用同一实现。普通权限运行检查并点击其按钮；legacy 模式用于复现旧 ShellExec 自重启错误，native 模式应弹出 UAC，子进程记录 Admin=1 并在安装前退出。不要把零文件安装的完成状态当作提权成功；须核对 Native 成功记录和管理员子进程。正式包还应从普通目录页触发提权，核对新进程的原账户、目录和 HKCU 安装语义；UAC 必须由用户处理。

检查默认 METHOD=check，仅验证目录页路径可读取，不提权；需要交互时显式传 /METHOD=native。CreateAppDir=yes 用于让目录页真实出现，PrepareToInstall 始终阻止实际安装。测试结束后须核对自己启动的父子 PID 已退出；异常窗口也属于待清理的测试进程，不能只关闭沙盒便视为收尾完成。

安装升级回归入口为 tools/test/sandbox/Test-InstallerUpgradeSandbox.ps1；用 -GuestScript 指定同目录 Test-InstallerRecoveryGuest.ps1，-PreviousInstaller 指向历史安装包，-CandidateInstaller 指向当前包，-OutputDirectory 使用全新报告目录。默认脚本保留旧版故障诊断行为，不能将其失败预期当成新版本成功回归。沙盒关闭网络与剪贴板，仅映射测试输入及结果，不映射本机安装目录或用户数据。升级协调器使用内置 Windows PowerShell 5.1，源文件保留 UTF-8 BOM；构建会核对编码并固定内嵌脚本 SHA-256。

- HotspotPage.xaml/.cs 与 HotspotPage 类名对应；工具箱主窗口是 ToolboxWindow。热点仅在用户点击按钮时开关。
- 产品源码在 src，功能检查和模拟客户端在 tests，工程脚本在 tools，安装定义在 installer，说明在 docs。生成文件、历史发布、本机证据在被 Git 忽略的 artifacts；保留用户提供的 docs/images。旧生成缓存不作为源码工程保留。
- 改动扫描、模型、指纹、存储和权限时跑功能检查；改分发时核对完整文件并做干净系统运行。仅文档变化无需重复全部控制测试。
- 不把旧快照、取消授权、部分成功或界面截图写成完整管理员扫描、真实重启轨迹或原生材质验收。

## 产物维护

布局由 tools/common/Artifacts.ps1 维护。artifacts 顶层仅允许 current、archive、reports、backups、temp、README.md 和 index.json，test.ps1 会检查。旧布局迁移脚本 tools/maintenance/Migrate-LegacyArtifacts.ps1 默认只列清单，-Apply 才执行；-RemoveBuildCaches 另清理可重建 bin/obj。旧构建先逐文件验证 ZIP 再删除展开目录，拒绝活动进程/服务路径和重解析点。私有原件以目录搬迁保留，不混进通用分发包。

## 开源维护

贡献、私密漏洞报告和评审见 [CONTRIBUTING](../CONTRIBUTING.md)、[SECURITY](../SECURITY.md)。依赖锁定、标签发布、SBOM 与校验见 [发布维护](RELEASING.md)，安全检查边界见 [安全审查](SECURITY-REVIEW.md)。CI 不上传整个 artifacts；真实网络、服务和管理员测试继续使用隔离环境。
