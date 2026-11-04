# 发布与包管理

## 版本与依赖

产品版本只修改 Directory.Build.props，并更新 CHANGELOG 和必要的当前文档。NuGet 版本集中放在 Directory.Packages.props，SDK 放在 global.json。协议、数据库、第三方客户端和夹具版本独立维护。

每个工程提交 packages.lock.json。更新依赖时重新还原并审查锁文件，CI 使用 locked mode、公共 NuGet 和全部传递依赖漏洞检查。Dependabot 分别维护 NuGet 与 Actions 更新，不自动合并。Core/Engine 是内部实现，不向 NuGet/GitHub Packages 发布冒充稳定公共 SDK。

## 正式发布

1. 用 PR 合入版本、源码、说明和锁文件；等待 `CI required` 通过，并完成与改动相关的 Sandbox/人工运行验收。
2. 在 main 已包含的提交创建注解标签 `vX.Y.Z` 并推送。标签必须与 Directory.Build.props 一致，不重用已公开版本。
3. Release 工作流在 Windows 构建器中再次检查源码/历史、依赖和产品功能，验证官方签名的 Inno Setup 编译器，构建完整安装/便携包并逐文件校验。
4. 为产品依赖生成 CycloneDX 1.6 SBOM、WinGet/Scoop 清单、版本来源元数据与 SHA256SUMS。SBOM 包含解析到的构建依赖，不能将其每一项都解释成实际加载的运行 DLL；具体交付文件看 files.sha256 与第三方 Notices。
5. 通过 GitHub OIDC 生成来源和 SBOM 证明，全部附件上传到草稿后才公开。只允许重试相同提交的草稿；公开版本不覆盖，修正必须发新版本。管理员启用 immutable releases 后，还由 GitHub 锁定公开附件和标签。

工作流使用短期 GITHUB_TOKEN，普通 CI 默认 contents:read，只有发布 job 获得发布和 OIDC 权限。分发不需要开发者 PAT、API 密钥或代码签名私钥。Actions 以完整提交 SHA 固定，并由 Dependabot 提供更新 PR。

## 下载验证

先核对 `Get-FileHash -Algorithm SHA256 <下载文件>` 与 Release 的 SHA256SUMS.txt，再可使用 GitHub CLI：

```powershell
gh attestation verify <下载文件> -R lizhengyi3003/FeiChuan-Toolbox
```

证明的机制见 [GitHub 官方文档](https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations)。来源证明不能代替代码审查、运行验收或 Windows Authenticode。当前产品未做 Authenticode 签名。

## 包分发与源码

官方交付渠道为 GitHub Releases，提供安装器、完整便携 ZIP、校验值、清单和同标签对应源码。自有代码按 GPL-3.0-only，第三方组件保留其声明。重新分发修改版须履行相应源码和声明义务。

Release 中的单文件 WinGet 清单和 Scoop JSON 供审核/本地使用，并不表示已提交社区仓库或商店。提交公共索引需要独立完成其验证和评审，不通过 CI 擅自向外部项目提 PR。

本地开发打包仍使用 `tools/build/Build-Release.ps1`，默认要求已提交源码；`-WorkingTree` 仅作本地验证，会记录 sourceDirty=true。artifacts/current 是本地当前产物，不把整个 artifacts 上传到 Release。

## Wiki

维护 docs/wiki 中的已审查页面，经 main 的路径触发同步。GitHub 要求先在网页创建首个 Wiki 页面，再支持 Git 同步；未初始化时工作流会明确失败并保留版本化源文档。工作流只同步这些页面，不清空其他 Wiki 内容。
