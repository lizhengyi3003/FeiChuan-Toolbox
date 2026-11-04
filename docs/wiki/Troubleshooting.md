# 故障排查

先记录产品版本、Windows 构建号、操作编号、错误码和发生步骤。在全局设置的日志查看器按类别/严重等级筛选；读取可重试，启停和注入不应盲目重复。

数据库损坏时保留原件，通过恢复入口核验；不要把空数据库当作修复。安装失败保留安装日志和程序文件备份。热点与代理故障先区分 DNS、上游、驱动和系统联网探测，不擅自重置工作的网络。

普通问题提交 [Issue](https://github.com/lizhengyi3003/FeiChuan-Toolbox/issues/new/choose)，安全问题使用 [私密报告](https://github.com/lizhengyi3003/FeiChuan-Toolbox/security/advisories/new)。完整 [排查指南](https://github.com/lizhengyi3003/FeiChuan-Toolbox/blob/main/docs/TROUBLESHOOTING.md) 包含已知限制。
