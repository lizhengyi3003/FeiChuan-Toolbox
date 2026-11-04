# 参与开发

欢迎报告问题、补充文档和提交修复。参与者遵守 [行为规范](CODE_OF_CONDUCT.md)，自有贡献按 [GPL-3.0-only](LICENSE) 提交；第三方代码须说明来源并保留其许可。

## 开始

1. 使用 Windows 11 x64，安装 [global.json](global.json) 指定的稳定版 .NET SDK、Windows SDK 26100 和 Visual Studio C++/WinUI 构建组件。
2. Fork 仓库并从 main 创建功能分支。执行 `./test.ps1`、`./build.ps1`；详细环境见 [开发说明](docs/DEVELOPMENT.md)。
3. NuGet 版本由 Directory.Packages.props 管理。修改依赖后重新生成并检查 packages.lock.json；CI 使用锁定还原。
4. 只提交源码和可公开的测试资料。不要提交启动数据库、完整扫描结果、真实账号、订阅、设备标识、证书或诊断原件。

## 提交与评审

提交标题使用 `类型: 具体改动`，例如 `fix: verify backup before stopping client`。常用类型：feat、fix、refactor、test、build、ci、docs、chore、security。不要为整理记录修改历史时间或虚构签名。普通改动经 PR 合并，推荐 squash；提交说明交代问题、行为变化、验证和限制。

PR 应通过工程检查、Windows 构建/测试、CodeQL 和供应链检查。权限、IPC、注册表、服务、安装器、恢复和网络改动需补充相应隔离证据。UI 测试和管理员 UAC 不可由普通 CI 编译结果替代，运行范围见 [验收记录](docs/VALIDATION.md)。

修改第三方软件配置前必须预览、备份并校验；保留账号和订阅。不要关闭贡献者真实使用的代理或远控服务来验证退出逻辑。故障重试必须保留操作编号、日志和恢复入口。

## 报告问题

普通问题使用 Issues 表单，附版本、Windows 构建号、最小复现和脱敏日志。先检查重复问题。安全漏洞通过 [安全策略](SECURITY.md) 的私密入口提交，勿公开可用凭据和利用细节。

发布流程、版本、软件清单与下载验证见 [发布维护](docs/RELEASING.md)。独立共享库尚未作为公共 API 发布，请勿把内部 Core/Engine 当作稳定 NuGet SDK。
