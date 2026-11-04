# 第三方组件与工具

| 组件 | 本次使用版本 | 来源与说明 |
| --- | --- | --- |
| .NET runtime | 10.0.14 / win-x64 | Microsoft；由 NuGet runtime 包进行自包含发布 |
| Microsoft.WindowsAppSDK.WinUI | 2.6.4 | Microsoft NuGet；包含 Foundation、Base、InteractiveExperiences 等传递依赖 |
| Microsoft.Web.WebView2 | 1.0.3719.77 | WinUI 的传递依赖，本应用界面使用原生控件 |
| Windows SDK | 10.0.26100 | Microsoft 构建工具及 Windows API 引用 |
| Inno Setup | 7.1.0 x64 | jrsoftware 官方签名发布；仅作为本地安装编译器 |

各组件遵守其原始包中的许可条款。应用运行依赖的原始许可和声明保留在源码与发布目录的 `Notices` 文件夹，并随安装包分发。本地 NuGet 原包保留在 `.tools/winui-build-tools/feed`；Inno 工具包保留自己的 license.txt。不要将开发缓存误打包为应用程序文件。

应用图标由本项目绘制，SVG 和生成脚本随源码提供；没有引入第三方图标素材。项目自有源码采用 [GPL-3.0-only](../LICENSE)。第三方依赖、微软系统组件及可再分发运行时仍按各自原许可分发，不因项目许可证而重新授权。正式包包含项目 LICENSE.txt 与第三方 Notices；对应源码可从同版本 Git 标签获取。
