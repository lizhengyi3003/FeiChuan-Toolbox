# 工程目录与维护规则

项目采用按职责分层、层内按功能组织的结构。三个产品程序集保持既有身份；功能检查与隔离测试工程放在 `tests`。本次调整保留现有协议、数据格式、服务名、应用标识和配置模板资源名。

```text
TunHotspot/
├─ TunHotspot.slnx                 # Visual Studio 解决方案，分开 src / tests / 文档
├─ Directory.Build.props          # 产品版本、目标框架、编译约定
├─ Directory.Packages.props       # NuGet 依赖版本的唯一入口
├─ NuGet.Config                   # 本地 feed 与官方源
├─ build.ps1                      # 普通构建 / 自包含发布
├─ test.ps1                       # 工程引用检查 + 现有功能检查
├─ src/
│  ├─ TunHotspot.Core/
│  │  ├─ Contracts/               # 序列化约定、版本与有限 IPC
│  │  ├─ Startup/
│  │  │  ├─ Models/               # 扫描、条目与监测数据模型
│  │  │  ├─ Scanning/Providers/   # 登录、服务、任务、WMI 等读取实现
│  │  │  ├─ Analysis/             # 分类、来源、筛选与分析结果模型
│  │  │  ├─ Monitoring/           # 持续观察与注册表订阅
│  │  │  └─ Control/              # 单项控制请求、备份模型及控制策略
│  │  ├─ Storage/                 # SQLite、事务恢复、迁移与数据路径
│  │  ├─ Settings/                # 独立偏好与本应用登录启动
│  │  ├─ Diagnostics/             # 统一诊断
│  │  ├─ Installation/            # 更新锁与本软件退出事件
│  │  ├─ Security/                # 进程身份、私有文件与原子写入
│  │  ├─ Hotspot/                 # 工作进程、状态、稳定观察与重连策略
│  │  ├─ Networking/              # 受限网络诊断及修复
│  │  └─ Configuration/Kumiryo/   # 模板、预检、事务、客户端与重开
│  │     └─ Resources/            # 筛选后的可分发配置模板
│  ├─ TunHotspot.Engine/
│  │  ├─ Program.cs              # 命令模式分派
│  │  ├─ Installation/            # 组件版本安装、受控目录与日志权限
│  │  ├─ Monitoring/              # 机器采集服务
│  │  ├─ StartupControl/          # 有限管理员操作
│  │  ├─ Auditing/                # 双轮只读复核
│  │  ├─ Mcp/                     # 队列、证据和建议接口
│  │  └─ Testing/                 # 仅受控沙盒条件下使用的测试服务入口
│  └─ TunHotspot.WinUI/
│     ├─ App.xaml(.cs)            # 生命周期、单实例与故障入口
│     ├─ Shell/                   # 主窗口、导航和生命周期
│     ├─ Features/                # Startup / Hotspot / Configuration / Settings
│     ├─ Controls/                # 共享布局、卡片、列表和按钮辅助
│     ├─ Infrastructure/          # Elevation / Notifications / Tray / Diagnostics
│     ├─ Testing/                 # 隔离 QA 路由与实际 XAML 截图入口
│     ├─ Assets/                  # 产品图片和图标
│     └─ Notices/                 # 随运行目录分发的许可
├─ tests/
│  ├─ TunHotspot.Tests/           # 控制台功能检查，Cases 保存独立专题检查
│  └─ Fixtures/                   # Kumiryo.Client / Kumiryo.Integration 隔离测试工程
├─ tools/
│  ├─ common/                    # 临时 SDK 环境及产物目录规则
│  ├─ build/                     # 正式发布、安装器和分发准备
│  ├─ assets/                    # 图标生成工具
│  ├─ maintenance/               # 有清单与校验的旧产物迁移
│  └─ test/
│     ├─ Test-Repository.ps1      # 引用、中央依赖、脚本语法与文档链接检查
│     ├─ ui/                     # 本机隔离 QA 实例与 UI Automation
│     ├─ sandbox/                # Sandbox 启动器和仅来宾可运行的脚本
│     └─ local/                  # 带正式安装保护的宿主安装测试
├─ installer/                    # Inno 定义、共享原生提权模块与内嵌 PowerShell 升级协调器
├─ docs/                         # 当前架构/开发/验收索引
│  ├─ guides/                    # 使用手册与功能范围
│  ├─ reports/                   # 有日期的修复和工程报告
│  └─ images/                    # 文档图片
├─ artifacts/                    # Git 忽略：current / archive / reports / backups / temp
└─ .tools/                       # Git 忽略：本机 SDK/编译器/依赖缓存
```

## 依赖与拆分约定

- UI、Engine 和测试依赖 Core；Core 不反向引用这些项目。WinUI 控件、窗口和通知留在 UI 层；SCM 安装和管理员执行留在 Engine。
- `ToolboxWindow` 的导航、生命周期、启动页面、控制、分析、设置和托盘分别使用 partial 文件。公共状态仍由同一窗口拥有，未引入第二套窗口状态。新增独立页面优先放到相应 `Features`，不要继续堆进主窗口构造函数。
- `WindowsScanner` 保留统一覆盖记录、取消与阶段编排；各采集来源放到 `Scanning/Providers` 的 partial 文件。下一次增加来源时同时维护阶段列表及覆盖说明。
- 保留 `TunHotspot.Core`、`TunHotspot` 等既有命名空间及 XAML `x:Class`。目录表示职责，partial 声明必须与原类型一致，避免为改目录而改变 IPC 或资源身份。
- XAML 图片使用 `ms-appx:///Assets/...`，不依赖页面所在目录。内置 Kumiryo 模板物理路径已调整，嵌入资源逻辑名继续使用 `TunHotspot.Core.KumiryoTemplate.json`。
- `KumiryoPrivateFiles` 保持类名兼容，实际供配置、偏好及存储共同使用，因此归属 `Security`。

## 产物和本机数据

当前入口固定为 `artifacts/current`，内部只放 `app`、`packages` 和 `release.json`，代表同一轮验证通过的构建。开发预览到 `artifacts/temp/preview/app`，正式或本地完整包均先在 temp 暂存和核验，成功后切换 current。

`archive/releases/<版本>` 保存历史安装包、便携包及原校验；`archive/builds` 保存旧展开构建的完整 ZIP、逐文件清单和 SHA-256。`reports` 保存测试与修复证据；`backups` 保存数据库、设置和源码原件；`temp` 保存可重新生成的中间产物。旧顶层路径已经迁移，去向见 `artifacts/reports/cleanup-20261103/migration.json`。

当前产物使用产品版本 1.5.3。历史报告和归档保留当时版本，不伪装成新版本；Kumiryo 0.2.4、依赖版本、协议和数据库版本也不机械改为产品版本。

运行数据 `%USERPROFILE%/.FeiChuanToolbox` 和受控 `%ProgramData%/TunHotspot` 不属于仓库整理范围。不能为了清理项目目录而删除用户库、原始备份、系统服务或安装版文件。

## 入口

```powershell
.\build.ps1 -Offline
.\test.ps1 -Offline
.\build.ps1 -Publish -Offline -OutputDirectory artifacts\temp\my-review
.\tools\build\Build-Installer.ps1 -PublishDirectory artifacts\temp\my-review
# 正式发布要求源码已提交，并使用新的空目录
.\tools\build\Build-Release.ps1 -Offline
```

完整流程与安全条件见 [开发说明](DEVELOPMENT.md)。
