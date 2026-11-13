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
    private void Navigation_SelectionChanged(NavigationView sender,NavigationViewSelectionChangedEventArgs args)
    {if(_initialized && args.SelectedItem is NavigationViewItem item){_page=item.Tag?.ToString()??"overview";if(_page=="entries"&&!_routeWithFilter)_entryFilter.Clear();if(_page=="pending"&&!_routeWithFilter)_pendingFilter.Clear();RenderPage();}}
    private void StartupMode_Click(object sender,RoutedEventArgs e)=>Navigate(_lastStartupPage);
    private void HotspotMode_Click(object sender,RoutedEventArgs e)=>Navigate("hotspot");
    private void ConfigurationMode_Click(object sender,RoutedEventArgs e)=>Navigate("configuration");
    private void SettingsMode_Click(object sender,RoutedEventArgs e)=>Navigate("settings");
    private void Navigate(string page,bool preserveFilter=false)
    {
        var item=Navigation.MenuItems.Concat(Navigation.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(x=>x.Tag?.ToString()==page);
        if(!preserveFilter&&page=="entries")_entryFilter.Clear();
        _routeWithFilter=preserveFilter;
        try{if(item is not null){if(ReferenceEquals(Navigation.SelectedItem,item)){_page=page;RenderPage();}else Navigation.SelectedItem=item;}else {_page=page;RenderPage();}}
        finally{_routeWithFilter=false;}
    }
    private void RenderPage()
    {
        try
        {
            if(_qa&&!_qaPageErrorInjected&&_page=="entries"&&Environment.GetCommandLineArgs().Contains("page-error-interactive"))
            {_qaPageErrorInjected=true;throw new IOException("QA 隔离页面读取失败，允许重试读取。");}
            RenderPageCore();
        }
        catch(Exception ex)
        {
            var incident=_diagnostics.Record("读取页面："+_page,ex,true);
            var retry=Button("重试读取此页",RenderPage);Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(retry,"Error.Retry");
            var details=new TextBox{Text=$"错误编号：{incident.Id}\n{incident.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {incident.Operation}\n{incident.Code}\n{incident.Guidance}\n{incident.Details}\n日志：{incident.LogPath}\n{incident.StorageWarning}",IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MaxHeight=300};
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(details,"Error.Details");
            ErrorWorkspace.Content=Scroll(Column(Text("此页暂时无法显示",26,true),Text("当前操作未完成；没有自动重复系统修改。可重试读取，或打开全局设置和诊断日志。"),details,
                Row(retry,Button("全局设置",()=>Navigate("settings")),Button("打开诊断日志",()=>Process.Start(new ProcessStartInfo(Path.GetDirectoryName(incident.LogPath)??_diagnostics.Folder){UseShellExecute=true})))));
            Navigation.Visibility=HotspotWorkspace.Visibility=ConfigurationWorkspace.Visibility=SettingsWorkspace.Visibility=Visibility.Collapsed;
            ErrorWorkspace.Visibility=Visibility.Visible;
        }
    }
    private void RenderPageCore()
    {
        if(!_initialized)return;
        ErrorWorkspace.Visibility=Visibility.Collapsed;ErrorWorkspace.Content=null;
        _refreshEntries=null;
        PageHost.Content=null;
        HotspotWorkspace.Content=null;
        ConfigurationWorkspace.Content=null;
        SettingsWorkspace.Content=null;
        bool hotspot=_page=="hotspot",configuration=_page=="configuration",settings=_page=="settings",standalone=hotspot||configuration||settings;
        StartupModeButton.IsChecked=!standalone;HotspotModeButton.IsChecked=hotspot;ConfigurationModeButton.IsChecked=configuration;SettingsModeButton.IsChecked=settings;
        ScanHeader.Visibility=standalone||_startupUnavailable?Visibility.Collapsed:Visibility.Visible;
        Navigation.Visibility=standalone?Visibility.Collapsed:Visibility.Visible;
        Message.Visibility=hotspot||configuration?Visibility.Collapsed:Visibility.Visible;
        HotspotWorkspace.Visibility=hotspot?Visibility.Visible:Visibility.Collapsed;
        ConfigurationWorkspace.Visibility=configuration?Visibility.Visible:Visibility.Collapsed;
        SettingsWorkspace.Visibility=settings?Visibility.Visible:Visibility.Collapsed;
        VersionLabel.Text=Contract.ProductVersion+" · "+(hotspot?"兼容热点":configuration?"Kumiryo 一键配置":settings?"全局设置":"启动管理");
        if(settings){SettingsWorkspace.Content=SettingsPage();return;}
        if(hotspot){HotspotWorkspace.Content=_hotspotHost;return;}
        if(configuration){_configuration??=new ConfigurationPage(this,_qa);ConfigurationWorkspace.Content=_configuration;return;}
        if (_startupUnavailable)
        {
            VersionLabel.Text = Contract.ProductVersion + " · 启动数据待修复";
            var details = Text(_startupError, 12);
            details.IsTextSelectionEnabled = true;
            var openLogs = Button("打开诊断目录", () => Process.Start(new ProcessStartInfo(
                Path.GetDirectoryName(_startupDiagnosticPath) ?? _diagnostics.Folder) { UseShellExecute = true }));
            openLogs.IsEnabled = Directory.Exists(Path.GetDirectoryName(_startupDiagnosticPath) ?? _diagnostics.Folder);
            PageHost.Content = Scroll(Column(Text("启动管理数据暂时不可用", 26, true),
                Text("原数据库已保留，没有重置历史或把空数据当作扫描结果。当前界面的启动管理读取、写入与监测已暂停；后台机器组件独立运行。热点及 Kumiryo 一键配置仍可使用。"),
                details,
                Row(Button("打开 Kumiryo 一键配置", () => Navigate("configuration")), Button("打开兼容热点", () => Navigate("hotspot")),
                    Button("重新核验数据库", () => { if(Application.Current is App app)app.RetryMainWindow(); }),
                    openLogs,
                    Button("查看原数据目录", () => Process.Start(new ProcessStartInfo(DataPaths.UserRoot) { UseShellExecute = true })))));
            return;
        }
        _lastStartupPage=_page;
        PageHost.Content=_page switch {"entries"=>Entries(),"codex-origin"=>Entries(agent:"Codex"),"dsh-origin"=>Entries(agent:"DSH"),"apps"=>Apps(),"groups"=>Groups(),"runtime"=>Runtime(),"pending"=>Pending(),"alerts"=>Alerts(),"changes"=>Changes(),"controls"=>Controls(),"settings"=>SettingsPage(),_=>Overview()};
    }
}
