# 常见问题

## TUN 未连接

在 Kumiryo 中检查 TUN 是否开启。此版本识别名为 `Kumiryo` 且处于 Up 状态的网卡；其他客户端或重命名网卡未验证。助手不会替你开启 TUN。

## Windows 显示“热点已开启”，但网络不通

确认电脑原本可以联网，然后手动关闭普通热点，通过助手的“开启兼容热点”重新开启。成功状态下手机继续使用原热点名称和密码，无需重新配对。

本机历史对照观察到：普通热点与 TUN 同开时 WLAN 的 forwarding 变为 Enabled 并伴随全断网；兼容入口成功后 WLAN 为 Disabled、TUN 和热点为 Enabled。该观察不等同于已经抓包证明 Windows 内核的全部成因，勿据此手改所有网卡转发状态。

## Windows 返回操作错误

等待 Windows 当前切换完成后按提示手动重试。若需要恢复，可在助手中点击“关闭热点”，也可以从 Windows 快捷设置关闭热点。所有会重启 Kumiryo 或断开连接的动作都由使用者手动执行。

## exe 单独复制后打不开

使用当前安装包，或完整解压 `artifacts/distribution` 中的当前便携包。必须保留 Engine、WinUI、.NET 运行库、PRI、Assets 及其他依赖，不能只复制 exe。`artifacts/win-x64` 是保留的 1.0.1 历史版，无需另行安装开发工具。

## 安装器提示助手正在运行

先从托盘菜单选择“退出”，再继续安装或卸载；仅关闭窗口默认收起到托盘。退出保留当前热点，Kumiryo 可继续运行。安装器不会强制结束其他进程。

## 图标暂时显示旧样式

重新打开快捷方式。应用窗口、exe 和安装包都已内置 ICO；Windows 可能暂时缓存旧的桌面图标，不需要为此重启代理或断开网络。

## 查看记录

工具箱的扫描、建议、告警、来源标记及两轮审计在 `%LOCALAPPDATA%\TunHotspot.Toolbox`。总览显示当前权限和缺口；“最近管理员复核”是历史复核结果，不保证之后未变化。待分析保留项可能缺少当前或目标证据，不能通过猜测结论清空。

操作记录：`%LOCALAPPDATA%\TunHotspot.WinUI\operations.jsonl`。
当前状态：`%LOCALAPPDATA%\TunHotspot.WinUI\state.json`。

`user-start-request` / `user-stop-request` 是用户按钮操作；`user-operation-completed` 表示 API 成功完成。早期开发记录可能包含已经修复的启动错误，应结合时间及当前进程判断。日志不记录热点密码、SSID、MAC 或订阅凭据。
