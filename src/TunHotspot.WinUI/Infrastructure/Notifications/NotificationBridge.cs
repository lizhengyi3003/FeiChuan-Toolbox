using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using TunHotspot.Core;

namespace TunHotspot;

internal static class NotificationBridge
{
    internal static string Status { get; private set; } = "未注册";
    internal static event Action<string>? Invoked;
    private static bool _registered;
    private static void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        if (args.Arguments.TryGetValue("eventId", out string? id) && Guid.TryParseExact(id, "N", out _)) Invoked?.Invoke(id);
    }
    internal static void Register()
    {
        try
        {
            if (_registered) return;
            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked += OnNotificationInvoked;
            try { manager.Register(); _registered = true; }
            catch { manager.NotificationInvoked -= OnNotificationInvoked; throw; }
            Status = AppNotificationManager.Default.Setting.ToString();
        }
        catch (Exception ex) { Status = "通知注册不可用：" + ex.Message; AppLog.Write("notification-registration", new { ex.Message }); }
    }
    internal static string Send(AlertEvent alert)
    {
        if (!_registered) return "发送失败：" + Status;
        try
        {
            Status = AppNotificationManager.Default.Setting.ToString();
            if (AppNotificationManager.Default.Setting != AppNotificationSetting.Enabled) return "系统通知不可用：" + Status;
            var notification = new AppNotificationBuilder().AddArgument("eventId", alert.Id)
                .AddText(alert.Title).AddText($"期望：{alert.Expected}；实测：{alert.Actual}。详情已保存在工具箱。")
                .BuildNotification();
            notification.Tag = alert.EntryId.Length > 16 ? alert.EntryId[..16] : alert.Id[..16]; notification.Group = "StartupWatch";
            AppNotificationManager.Default.Show(notification); return "已提交系统（不代表已查看）";
        }
        catch (Exception ex) { return "发送失败：" + ex.Message; }
    }
    internal static void Close()
    {
        if (!_registered) return;
        _registered = false;
        try
        {
            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked -= OnNotificationInvoked;
            manager.Unregister();
        }
        catch (Exception ex) { AppLog.Write("notification-unregister-error", new { ex.Message }); }
    }
}
