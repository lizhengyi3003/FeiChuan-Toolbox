using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using TunHotspot.Core;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.UI;
using Windows.UI.ViewManagement;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace TunHotspot;

public sealed partial class ToolboxWindow : Window
{
    internal sealed record OverviewData(int Watches, int Unread, int Pending, AuditComparison? Audit)
    {
        internal static OverviewData Read(ToolboxStore store) =>
            new(store.Watches.Count, store.Alerts.Count(alert => !alert.IsRead), store.Pending.Count,
                store.Get<AuditComparison>("lastAdminAudit"));
    }

    private UIElement Overview()
    {
        var snapshot=_snapshot;var body=Column(Text("启动与后台，一处看清",28,true),Text("扫描系统配置、核对实际运行，并把不确定的项目交给 Codex 分析。"));
        if(snapshot is null){body.Children.Add(Card(Text("正在建立首次基线。首次扫描已有的项目不会全部被报成新增。")));return Scroll(body);}
        var counts=snapshot.Entries.GroupBy(x=>x.Category).OrderByDescending(x=>x.Count());
        body.Children.Add(Card(Column(Text($"{snapshot.Entries.Count:N0} 个配置入口 · {snapshot.Processes.Count:N0} 个进程快照",22,true),
            Text($"{snapshot.Privilege} · {snapshot.Finished.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {(snapshot.Cancelled?"已取消":snapshot.Complete?"所列采集范围完成":"流程完成，存在采集缺口")}"),
            Text($"本次读取 {snapshot.Entries.Count(x=>x.ObservationStatus=="本次读取"):N0} 项；另保留未能再次确认的历史入口 {snapshot.Entries.Count(x=>x.ObservationStatus!="本次读取"):N0} 项。",12),
            Text(string.Join("   /   ",counts.Select(x=>$"{x.Key} {x.Count()}"))))));
        var summary = _overviewData;
        var monitoring = _serviceConnected ? "已连接机器组件" : _monitor.State;
        body.Children.Add(Card(Column(Text("监测与告警",18,true),Text(summary is null ? $"采集：{monitoring}" :
            $"采集：{monitoring}\n关注 {summary.Watches} 项，未读告警 {summary.Unread} 项，待分析 {summary.Pending} 项。"),
            Row(Button("查看全部入口",()=>Navigate("entries")),Button("查看通知与关注",()=>Navigate("alerts"))))));
        if(summary?.Audit is AuditComparison audit)
            body.Children.Add(Card(Column(Text("最近管理员复核",18,true),Text($"{audit.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {audit.Privilege} · {audit.Entries} 项\n连续两轮：新增 {audit.Added.Count}、消失 {audit.Removed.Count}、配置/目标指纹变化 {audit.Changed.Count}；覆盖说明 {audit.Gaps.Count} 条。"),Text("仅代表该次复核窗口内的观察；覆盖缺口和之后的变化仍需查看扫描记录。",12))));
        body.Children.Add(Text("采集覆盖",20,true));
        foreach(var c in snapshot.Coverage)
        {
            var card=Column(Text(c.Display,14,true));
            if(c.Note.Length>0||c.Issues.Count>0)card.Children.Add(new Expander{Header=c.Issues.Count>0?$"查看 {c.Issues.Count} 条覆盖说明":"查看覆盖说明",HorizontalAlignment=HorizontalAlignment.Stretch,Content=Text(c.Note+"\n"+string.Join("\n",c.Issues),12)});
            body.Children.Add(Card(card));
        }
        body.Children.Add(Text(snapshot.BootObservation,12));return Scroll(body);
    }
}
