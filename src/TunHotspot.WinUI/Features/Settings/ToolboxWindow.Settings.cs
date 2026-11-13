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
    private UIElement SettingsPage()
    {
        Button StartupOnly(Button button){button.IsEnabled=!_startupUnavailable;return button;}
        var title=Text("全局设置",28,true);Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(title,"Settings.Title");
        var subtitle=Text("调整工具箱的外观、字体、托盘行为与通知。");subtitle.Opacity=0.75;
        var materials=new ComboBox{Header="背景材质",ItemsSource=new[]{"Acrylic","Mica","MicaAlt","None"},SelectedItem=_settings.Backdrop,MinWidth=180};
        var themes=new ComboBox{Header="主题",ItemsSource=new[]{"跟随系统","浅色","深色"},SelectedItem=_settings.Theme,MinWidth=160};
        var font=new ComboBox{Header="字体（DirectWrite 自动枚举）",ItemsSource=_fonts,SelectedItem=_settings.Font,Text=_settings.Font,IsEditable=true,IsTextSearchEnabled=true,MinWidth=280,MaxDropDownHeight=320};
        font.Text=_settings.Font;
        font.PlaceholderText=_settings.Font;
        font.Loaded += async (_, _) =>
        {
            font.Text = _settings.Font;
            await (_fontLoad ?? LoadFonts());
            if (_exiting || !font.IsLoaded) return;
            font.ItemsSource = _fonts;
            font.Text = _settings.Font;
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(font,"Settings.Font");
        var size=new NumberBox{Header="正文字号",Value=_settings.FontSize,Minimum=10,Maximum=28,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Compact,Width=130};
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(materials,"Settings.Material");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(themes,"Settings.Theme");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(size,"Settings.FontSize");
        materials.SelectionChanged+=(_,_)=>{_settings.Backdrop=materials.SelectedItem?.ToString()??"Mica";SaveSettings();};themes.SelectionChanged+=(_,_)=>{_settings.Theme=themes.SelectedItem?.ToString()??"跟随系统";SaveSettings();};
        font.SelectionChanged+=(_,_)=>{if(font.SelectedItem is string name){_settings.Font=name;SaveSettings();}};size.ValueChanged+=(_,_)=>{if(double.IsFinite(size.Value)){_settings.FontSize=size.Value;SaveSettings();}};
        font.TextSubmitted+=(_,args)=>{string? match=_fonts.FirstOrDefault(x=>x.Equals(args.Text,StringComparison.CurrentCultureIgnoreCase));if(match is not null){font.SelectedItem=match;_settings.Font=match;SaveSettings();}else Tell("未找到该字体","请选择已枚举的字体，或安装字体后刷新列表。");args.Handled=true;};
        var tray=new ToggleSwitch{Header="关闭窗口时收起到托盘",IsOn=_settings.CloseToTray};tray.Toggled+=(_,_)=>{_settings.CloseToTray=tray.IsOn;SaveSettings();};
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(tray,"Settings.CloseToTray");
        var notify=new ToggleSwitch{Header="Windows 系统通知",IsOn=_settings.Notifications};notify.Toggled+=(_,_)=>{_settings.Notifications=notify.IsOn;SaveSettings();};
        StartupRegistrationState? loginState=null;string loginError="";
        try{if(!_qa)loginState=StartupRegistration.Read();}catch(Exception ex){loginError=DiagnosticLog.Clean(ex.Message);}
        var login=new ToggleSwitch{Header="登录 Windows 时启动飞船的小工具箱",IsOn=loginState?.Enabled??false,IsEnabled=loginError.Length==0};
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(login,"Settings.AutoStart");
        bool resettingLogin=false;
        login.Toggled+=async(_,_)=>
        {
            if(resettingLogin)return;bool requested=login.IsOn;
            try
            {
                if(_qa){resettingLogin=true;login.IsOn=!requested;Tell("隔离验证","未修改真实登录启动项。");return;}
                var before=StartupRegistration.Read();
                var preview=new ContentDialog{Title=requested?"开启登录自启":"关闭登录自启",PrimaryButtonText="确认",CloseButtonText="取消",XamlRoot=ShellRoot.XamlRoot,Content="仅修改当前账户的工具箱登录入口，并备份原状态；后台机器采集服务单独控制。"};
                if(await preview.ShowAsync()!=ContentDialogResult.Primary){resettingLogin=true;login.IsOn=before.Enabled;return;}
                var after=StartupRegistration.SetEnabled(requested,Environment.ProcessPath!,before.Fingerprint,AppLog.DataRoot);Tell("登录自启已核验",after.Description+"；原状态已保存在 autostart-changes。");
            }
            catch(Exception ex){ReportException(ex,"修改登录自启");resettingLogin=true;login.IsOn=!requested;}
            finally{resettingLogin=false;}
        };
        var diagnostics=new TextBox{IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MaxHeight=220,Text=string.Join("\n\n",_diagnostics.Recent().Select(x=>$"{x.Display}\n{x.Code} · {x.Guidance}\n{x.LogPath}"))};
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(diagnostics,"Settings.Diagnostics");
        NetworkHealthReport? networkReport=null;
        var networkText=new TextBox{Text="只读检测不会关闭代理或 TUN，不会重启网卡和服务。",IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MaxHeight=230};
        var networkCheck=AsyncButton("检测 Microsoft Store 网络",async()=>{networkReport=await NetworkHealth.Read();networkText.Text=string.Join("\n",networkReport.Checks);});
        var proxyFix=AsyncButton("预览并清除失效本地代理",async()=>
        {
            networkReport=await NetworkHealth.Read();if(!networkReport.CanClearStaleProxy){networkText.Text=string.Join("\n",networkReport.Checks);return;}
            var preview=new ContentDialog{Title="清除残留本地代理",Content="仅在 Kumiryo TUN 已关闭、代理指向本机且端口没有监听、未配置 PAC 时，备份后关闭当前用户代理启用标志。不会删除代理地址或绕过规则。",PrimaryButtonText="确认清除",CloseButtonText="取消",XamlRoot=ShellRoot.XamlRoot};
            if(await preview.ShowAsync()==ContentDialogResult.Primary){await NetworkHealth.ClearStaleProxy(networkReport.Fingerprint);networkReport=await NetworkHealth.Read();networkText.Text=string.Join("\n",networkReport.Checks);}
        });
        var storeCache=AsyncButton("修复 Store 缓存",async()=>{var preview=new ContentDialog{Title="修复 Microsoft Store 缓存",Content="Windows wsreset 会清理商店缓存并重新打开商店。不会卸载应用或重置网络；完成后请核验内容是否恢复加载。",PrimaryButtonText="执行系统缓存修复",CloseButtonText="取消",XamlRoot=ShellRoot.XamlRoot};if(await preview.ShowAsync()==ContentDialogResult.Primary){Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"wsreset.exe")){UseShellExecute=true});AppLog.Write("store-network-cache-reset-request",new{businessSuccessUnverified=true});networkText.Text="已启动 Windows 商店缓存修复；请等待商店重新打开后核验。";}});
        if(_qa){networkCheck.IsEnabled=proxyFix.IsEnabled=storeCache.IsEnabled=false;}
        var body=Column(title,subtitle,Card(Column(Text("外观",18,true),Row(materials,themes),Text("Mica 使用主题与壁纸形成背景，本身不透视其他窗口；Acrylic 提供毛玻璃效果。",12),Text(_materialState,12),Row(font,size),
            Row(AsyncButton("刷新字体列表",async()=>{await LoadFonts();RenderPage();}),Button("恢复默认外观",()=>{_settings.Backdrop="Mica";_settings.Theme="跟随系统";_settings.Font="Segoe UI Variable";_settings.FontSize=14;SaveSettings();RenderPage();})))),
            Card(Column(Text("托盘与通知",18,true),tray,notify,Text("通知状态："+NotificationBridge.Status,12),Row(Button("发送测试通知",SendTestNotification),Button("查看应用日志",()=>Process.Start(new ProcessStartInfo(AppLog.Folder){UseShellExecute=true}))))),
            Card(Column(Text("登录自启",18,true),login,Text(loginError.Length>0?loginError:loginState?.Description??"隔离验证：不写入系统启动项。",12),Text("此开关控制登录后的托盘界面，不随启动数据库故障而禁用。",12))),
            Card(Column(Text("包含开机的持续监测",18,true),Text(_startupUnavailable?"启动数据库当前不可用；此处的启动管理操作暂不可用，后台机器组件独立运行。":_serviceConnected?"机器组件已连接；关闭界面后仍采集，通知由登录托盘进程发送。":"尚未连接机器组件。当前提供普通权限范围，手动全局扫描可申请管理员采集。"),
                Row(StartupOnly(AsyncButton("安装/更新开机采集组件",()=>Component(true))),StartupOnly(AsyncButton("卸载采集组件",()=>Component(false))),StartupOnly(AsyncButton(_settings.Paused?"恢复监测":"暂停监测",Pause))),
                Text("安装时会弹 UAC，并添加本软件的机器服务和当前用户登录托盘入口。卸载组件保留历史数据及受控版本文件供恢复。",12))),
            Card(Column(Text("分析与数据",18,true),Text("应用设置：preferences.json；启动扫描与历史：toolbox.db。两者位于当前用户本地目录；机器采集日志位于管理员控制的 ProgramData。"),Row(StartupOnly(AsyncButton("连接 Codex",ConnectCodex)),StartupOnly(AsyncButton("导出扫描报告",ExportReport)),Button("打开应用数据目录",()=>Process.Start(new ProcessStartInfo(DataPaths.UserRoot){UseShellExecute=true}))),
                Text("Windows 11 x64 · 自包含发布 · AI 分析可选。常见登录入口、第三方任务及普通服务支持单项控制；驱动和特殊入口显示具体限制与系统配置入口。",12))),
            Card(Column(Text("Microsoft Store 与网络",18,true),Row(networkCheck,proxyFix,storeCache),networkText)),
            Card(Column(Text("日志查看器",18,true),new LogViewer(_diagnostics,this))),
            Card(Column(Text("诊断与恢复",18,true),Text(_startupUnavailable?"启动数据库暂不可用。应用设置与诊断仍可使用；重新核验不会重置数据。":_store.Get<string>("recoveryNotice")??"启动数据库已加载。诊断记录独立保存，不依赖扫描数据库。"),diagnostics,
                Row(Button("刷新诊断列表",RenderPage),Button("打开诊断目录",()=>{Directory.CreateDirectory(_diagnostics.Folder);Process.Start(new ProcessStartInfo(_diagnostics.Folder){UseShellExecute=true});}),Button("重新核验数据库",()=>{if(Application.Current is App app)app.RetryMainWindow();})),
                Row(AsyncButton("立即备份并核验数据库",async()=>
                {
                    if (_exiting || DatabaseWorkActive) return;
                    if (_startupUnavailable) throw new InvalidOperationException("当前数据库不可用，不能将损坏副本当作备份。");
                    var backup = Task.Run(_store.CreateVerifiedBackup);
                    TrackDatabaseWork(backup);
                    string path = await backup;
                    if (!_exiting) Tell("备份核验成功", Path.GetFileName(path));
                }),AsyncButton("从最近的有效备份恢复",async()=>
                {
                    if(!_startupUnavailable)throw new InvalidOperationException("当前数据库可用。为避免覆盖有效历史，只在数据库不可用时提供此恢复。");
                    var dialog=new ContentDialog{Title="从有效备份恢复",Content="将完整归档当前原件，再恢复最近一份完整性检查通过的备份。备份之后的历史可能不包含在内，需要复扫；不会创建空库。",PrimaryButtonText="确认恢复",CloseButtonText="取消",XamlRoot=ShellRoot.XamlRoot};
                    if (await dialog.ShowAsync() == ContentDialogResult.Primary && !_exiting && !DatabaseWorkActive)
                    {
                        var restore = Task.Run(() => DatabaseRecovery.RestoreLatest(AppLog.DataRoot));
                        TrackDatabaseWork(restore);
                        await restore;
                        if (!_exiting && Application.Current is App app) app.RetryMainWindow();
                    }
                })),Text("重试只重新读取。数据库采用事务刷盘、启动核验、每六小时内首次打开及正常退出备份，保留最近五份有效备份；原始修复归档不自动删除。",12))));
        body.Padding=new Thickness(24,8,24,24);body.Spacing=16;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(body,"Settings.Content");
        return Scroll(body);
    }
    private void SendTestNotification()
    {
        var alert=new AlertEvent{Title="工具箱测试通知",EntryId="notification-test",Expected="通知和记录可查看",Actual="测试事件",Evidence="本软件本地测试，不修改启动配置。"};
        if(!_startupUnavailable)_store.SaveAlert(alert);
        alert.Delivery=NotificationBridge.Send(alert);
        if(!_startupUnavailable)_store.SaveAlert(alert);
        AppLog.Write("notification-test",new{alert.Id,alert.Delivery,startupRecordAvailable=!_startupUnavailable});
        Tell("测试结果",alert.Delivery+(_startupUnavailable?"；记录已保存到应用日志。":""));
    }
    private void SaveSettings()
    {
        try{_preferences.Save(_settings);}
        catch(Exception ex){ReportException(ex,"保存应用设置");}
        ApplyAppearance();
    }
}
