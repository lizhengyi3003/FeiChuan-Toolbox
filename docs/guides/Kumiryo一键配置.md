# Kumiryo 一键配置

当前产品版本 1.6.0，提供 Kumiryo 0.2.4 和 0.2.5 的对应模板。

与“启动管理”“兼容热点”并列，全部页面、预览、进度和结果使用 WinUI 3。

## 迁移范围

| 模块 | 内容 |
| --- | --- |
| overrideDns | DNS 覆写开关 |
| globalAddedRules | 0.2.5 内置模板保留目标已有规则，空列表可填入 95 条内置规则；捕获或导入的普通模板按预览替换 |
| appSettingProps | 应用选项 |
| networkProps | 系统代理选择、绕过列表、网络和 DNS 选项 |
| vpnProps | VPN、IPv6 与访问控制 |
| themeProps | Kumiryo 主题 |
| proxiesStyleProps | 节点列表样式 |
| windowProps | 当前模板为自动大小/位置，避免搬入其他屏幕坐标 |
| patchClashConfig | TUN、DNS、端口、规则覆写与相关设置 |

账号、外层云配置/登录信息、profiles 及 currentProfileId 不迁移。目标电脑原有同类选项按预览合并，未知字段保留。通用包只有九模块模板，不含本机原始设置、订阅文件、云配置、登录信息或加密备份。

本机 `config.yaml` 不是可直接编辑的普通 YAML/JSON 配置；本功能操作 `shared_preferences.json` 内的 `flutter.config`，由客户端重新生成运行配置，不绕过客户端专用格式。

## 使用

1. 打开本页后自动识别当前账户配置和安装目录。未安装或版本低于 0.2.5 时会提示，确认后校验并打开内置安装向导。安装或升级完成后重新检测；新用户需在 Kumiryo 登录并初始化配置。
2. 查看模块核对和变更预览。可以使用内置模板，或捕获本机设置、导入/导出另一份已筛选模板。
3. 点击“预览并一键注入”，核对“只结束客户端界面”的兜底选项后确认，自动建立备份、退出客户端、写入并重新打开。取消该选项后，如果客户端只收起到托盘，操作会停止并提示手动退出。
4. 查看持久文件与运行核验明细。已一致时可只核验，也可明确确认后备份并重新加载；连接后可再次核验完整性。

路径来自当前用户目录、运行进程和安装登记。未安装、未初始化、版本不同、模块不完整、端口冲突、进程身份受限或路径包含重解析点时停止自动执行并说明原因。手动选择文件是为定位本机已有配置；不支持网络共享路径。

0.2.4 和 0.2.5 使用各自对应的模板；更高版本保留版本提示。安装包与便携包附带同一份 0.2.5 安装器，随项目更新维护固定哈希，未进行 Authenticode 签名。模板版本、规则数和字段可在页面核对。
本地和 GitHub 使用同一构建入口。发布时按文件名顺序拼接仓库中的安装器分片，核对原始 SHA-256 后再纳入分发清单。

### 0.2.5 设置对照

| Kumiryo 页面项目 | 持久配置和行为 |
| --- | --- |
| 覆写 DNS、系统 DNS | `overrideDns`、`networkProps.appendSystemDns`；0.2.5 模板移除旧 `autoSetSystemDns` |
| DNS 服务商、传输方式 | `patchClashConfig.dns` 的解析服务器及 URL 协议。内置模板保留自定义组合，主解析与代理解析使用阿里、腾讯 DoH |
| 一键优选 | 0.2.5 将优选结果写入默认、主解析和代理解析三组服务器列表。内置模板保留本机已有列表，空缺时使用模板默认值。首次优选仍在 Kumiryo 中开启 |
| 高级 DNS 状态、监听、使用 Hosts、使用系统 Hosts、IPv6、遵守规则、PreferH3 | `dns.enable`、`listen`、`use-hosts`、`use-system-hosts`、`ipv6`、`respect-rules`、`prefer-h3` |
| DNS 模式、FakeIP 范围、FakeIP 过滤 | `dns.enhanced-mode`、`fake-ip-range`、`fake-ip-filter` |
| 默认域名服务器、域名服务器策略、域名服务器、Fallback、代理域名服务器 | `dns.default-nameserver`、`nameserver-policy`、`nameserver`、`fallback`、`proxy-server-nameserver` |
| Fallback 过滤 GeoIP、GeoIP 代码、Geosite、IP/掩码、域名 | `dns.fallback-filter` 的 `geoip`、`geoip-code`、`geosite`、`ipcidr`、`domain` |
| 语言、主题模式、端口、应用 IPv6、局域网代理 | `appSettingProps.locale`、`themeProps.themeMode`、`patchClashConfig.mixed-port`、`patchClashConfig.ipv6`、`patchClashConfig.allow-lan` |
| 退出时最小化、自动运行、跳过倒计时、自启动、静默启动 | `appSettingProps.minimizeOnExit`、`autoRun`、`skipConnectionCountdown`、`autoLaunch`、`silentLaunch`。自启动的系统登记仍由客户端管理 |
| 系统代理、排除域名、TUN、栈模式 | `networkProps.systemProxy`、`bypassDomain`、`patchClashConfig.tun.enable`、`stack` |
| TUN 绕过局域网、TUN 绕过地址 | `networkProps.desktopBypassPrivateRoute`、`desktopBypassPrivateRouteAddress`。默认保留本机值，捕获或导入时可迁移 |
| 服务模式 | 读取 Kumiryo 辅助服务的实际运行状态，安装和服务由 Kumiryo 自己的向导管理 |

## 备份、恢复与兜底

执行可使用普通权限或原账户 UAC。遇到普通权限不能确认的客户端身份、文件访问权限时，可先点击“管理员核验”；它只返回差异和检查结果，不退出客户端或修改设置。确认注入后，执行组件另行取得 Windows 授权，取消则留档且不修改。

管理员模式只处理发起账户的标准 Kumiryo 设置目录，拒绝其他 Windows 账户提权及任意目录。自定义目录使用普通模式。工作进程在操作前核实并复制原普通调用进程令牌，或使用同一账户、同一会话的普通 Explorer 令牌，通过 [CreateProcessWithTokenW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createprocesswithtokenw) 重开客户端并复核权限。管理员权限不会被客户端继承。没有可用普通令牌时停止，不用高权限勉强重开。

观察进程独立等待同一个工作进程，并保留普通启动能力。关闭工具箱不取消已开始工作；工作进程被终止时，观察进程检查日志和实际文件后恢复。系统重启等使二者同时退出时，下次进入页面显示未完成记录，用户可检查并恢复。

写入前保存初始副本并解密读回核验，退出客户端后再保存稳定的原始/目标副本，用 [Windows DPAPI](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata) 在当前用户上下文加密；不调用 EFS 证书导出或修改文件加密策略。历史分别显示“初始原件已校验”和“写入恢复备份已校验”，前者存在不代表已经写入。通常需要原用户和原电脑恢复，因此跨电脑迁移的是无凭据模板，不是原始备份。

准备文件先完整写入并刷新，再原子替换。预览后和写入前均核对文件指纹；恢复仅撤回本次涉及的字段，并保留后来更新的账号、订阅和无关内容。如果相关字段后来又被修改，停止恢复、保留备份并显示失败原因，不能宣称恢复成功。

不再向 Flutter 线程发送 WM_QUIT。客户端已设置关闭收起到托盘时，跳过无效的关闭请求；其他情况先尝试正常窗口关闭。仅在本次预览明确授权、初始备份校验通过、配置保存稳定且进程路径、PID、句柄、创建时间、用户与会话一致时，允许结束该客户端界面进程。不会结束进程树、代理内核或服务；未授权或核验不符时停止写入并说明原因。该兜底可能丢失尚未保存的界面状态；客户端自身行为也可能短暂影响代理连接，预览中有说明。

## 完整性与日志

九模块保存结果与模板逐项比较，另核对客户端、内核、TUN、端口归属及系统集成限制。普通权限不能读取 SYSTEM 内核映像时，交叉核对受保护服务路径、SCM PID、子进程创建顺序和端口归属；不把同名进程直接视为内核。

当前客户端没有已验证的完整内核设置读回接口，因此界面保留“内核逐字段读回待核对”。系统代理、DNS、开机启动登记等副作用也不能由偏好文件值直接证明。文件完整、运行检查部分通过和全部运行检查通过是不同状态。未连接时不会冒充已生效。

本机目录为 `%USERPROFILE%\.FeiChuanToolbox\KumiryoConfiguration`。每条记录包含步骤、时间、完成项、未完成项、加密备份及恢复结果。可导出的日志只含状态和步骤，不包含配置原文、账号、订阅或凭据。页面支持历史选择、恢复备份及恢复中断操作。

## 验证边界

1.5.6 的 136 项功能检查通过。普通权限及管理员隔离原生客户端测试验证了未授权停止写入、经授权退出/重开、九模块写入、恢复和强制结束工作进程后的观察恢复；管理员流程确认重新打开的客户端没有继承管理员权限。真实远端核验及当前限制见 [当前验收](../VALIDATION.md)。沙盒测试客户端不运行代理、不修改系统网络，也不随用户程序分发。

不同硬件、真实 Kumiryo 版本差异、客户端内部重载行为和实际断电等仍需相应环境验证。不能把替身测试或文件检查宣称为所有客户端设置运行效果均已验证。

启动管理数据库无法读取时，主窗口明确显示原件保留和该部分暂停，使用独立临时外壳数据维持窗口，不显示假的空扫描结果。热点与本页不依赖该损坏数据库，依旧可以使用；不会为新增配置功能重置用户的启动历史。
