# GitHub 维护与发布审核

仓库的平台配置源为 [.github/repository-policy.json](../.github/repository-policy.json)。不要把平台页面上的开关和声明文件分开维护。

## 配置应用与复核

使用有仓库管理权限的 GitHub CLI 登录。先运行 `./tools/maintenance/Set-RepositoryPolicy.ps1` 预览，再用 `-Apply` 应用，最后运行 `./tools/maintenance/Test-RepositoryPolicy.ps1` 只读核对。后者读取仓库、安全、Actions、规则集、发布审批、标签范围和受管标签；权限不足或读回不一致直接报错，不将无法检查当作通过。它不会修复设置，也不向 CI 保存管理员 PAT。更改平台设置或正式发版前运行一次。

main 必须通过 PR；管理员绕过仅限 PR，不再允许直接推送绕过。正常合并仍须等所有检查通过。v* 标签仅管理员可创建，发布的标签和附件不可覆盖。当前只有一位维护者，批准数保持 0；加入第二位可信维护者后再启用至少一人批准、CODEOWNERS 和最后推送者之外的批准要求。

## 发布审批清单

release 环境只接受 v* 标签，要求仓库所有者人工确认，关闭管理员跳过环境审批。由于目前只有一位维护者，允许发布发起者本人确认；这是人工核对关口，不是独立双人审查。不要让自动化替维护者点击批准。

批准前逐项检查：

- 标签版本与 Directory.Build.props、CHANGELOG 和源码提交一致；同一提交的 CI required 已通过。
- 相关权限、数据迁移、安装、恢复和网络变更已有隔离运行验收记录；注明实际版本和提交，不只提供构建截图。
- 凭据、依赖和 CodeQL 检查已通过；评估新增依赖的许可证和运行时影响。
- 本次发布包含相应变更说明、升级注意事项和已知问题；无私人配置、原始日志或签名私钥。

批准后工作流重建包、生成证明、验证草稿、公开并回读地址。公开包仍须独立下载校验并在干净 Windows 11 中运行。当前环境审批不自动解析 Sandbox 证据，维护者须人工核对；不能把批准操作等同于测试成功。

## 社区与安全

错误和功能建议使用 Issue 表单；一般使用讨论放在 [Discussions](https://github.com/lizhengyi3003/FeiChuan-Toolbox/discussions)。安全漏洞使用 [私密报告](https://github.com/lizhengyi3003/FeiChuan-Toolbox/security/advisories/new)，不要公开凭据或漏洞利用细节。

扩展密钥模式扫描和密钥有效性检查列入 manual_checks 待办：本次 API 写入返回成功，但实际读回仍为 disabled，不能声称开启。需在 GitHub Settings 的 Security 页面确认是否可用并手动启用；核对脚本会明确提示未满足项。有效性检查可能向对应提供商验证支持类型的密钥，不能保证覆盖所有凭据。CI 扫描当前源码、完整历史和传递依赖，并定期运行；零告警不意味着无漏洞。

行为规范的非敏感问题可在 Discussions 联系 [仓库所有者](https://github.com/lizhengyi3003)；目前未设置专门的私密社区投诉邮箱，不要把安全漏洞入口当作一般投诉邮箱，也不要公开私人证据。

## 需要额外条件的事项

- 第二位独立维护者：加入后才能建立可持续的双人批准制度。
- Authenticode：须有真实代码签名证书或受信签名服务，建立凭据托管和签名验证后再强制；GitHub 来源证明不是 Windows 代码签名。
- 账号 2FA、Passkey、恢复码、个人令牌最小权限：由账号所有者在个人设置核对，本仓库脚本无法证明已配置。
- WinGet/Scoop 社区上架需要相应社区的独立审查；现有清单不等于已上架。

自动合并、Projects、额外 Webhook 和长期 PAT 均非当前流程必需，不因追求开关数量而开启。
