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
    internal async Task CaptureForQa(string page)
    {
        try
        {
            if(page=="settings-narrow")
            {
                double scale=GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this))/96.0;
                AppWindow.Resize(new SizeInt32((int)(760*scale),(int)(800*scale)));page="settings";
            }
            if (page == "recovery-interactive") { await Task.Delay(30000); page = "configuration"; }
            if (page == "settings-interactive") { await Task.Delay(30000); page = "settings"; }
            if(page=="reliability-interactive")
            {
                _diagnostics.Event("热点","警告","QA 热点过滤测试","只读界面测试");_diagnostics.Event("设置","信息","QA 设置过滤测试");
                Navigate("settings");await Task.Delay(30000);page="settings";
            }
            if (page == "page-error-interactive") { Navigate("entries"); await Task.Delay(16000); page = "entries"; }
            bool retainSelection=page=="controls-interactive";
            if(page=="controls-interactive")
            {
                await Task.Delay(1000);
                var target=(_snapshot?.Entries??[]).First(x=>x.Provider=="services"&&ControlPolicy.Reason(x,Contract.Sid).Length==0&&!ControlPolicy.RemoteOrProxy(x));
                _entryFilter.Search=target.Name;Navigate("entries",true);await Task.Delay(300);
                ListView? FindList(DependencyObject root)
                {
                    if(root is ListView list&&Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(list)=="Entries.List")return list;
                    for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)if(FindList(VisualTreeHelper.GetChild(root,i)) is ListView found)return found;
                    return null;
                }
                var list=FindList(PageHost)??throw new InvalidOperationException("条目列表尚未加载。");
                list.SelectedItem=((IEnumerable<StartupEntry>)list.ItemsSource).First(x=>x.Id==target.Id);
                await Task.Delay(20000);page="entries";
            }
            if(page=="filter-regression")
            {
                await Task.Delay(500);var single=(_snapshot?.Entries??[]).GroupBy(x=>x.App).OrderBy(x=>x.Count()).First();
                _entryFilter.App=single.Key;Navigate("entries",true);await Task.Delay(100);
                var filtered=_entryFilter.Apply(_snapshot!.Entries).Count;
                Navigate("overview");Navigate("entries");await Task.Delay(100);
                var all=_entryFilter.Apply(_snapshot.Entries).Count;
                if(all!=_snapshot.Entries.Count||filtered>all)throw new InvalidOperationException("应用筛选返回全部失败。");
                _entryFilter.App=single.Key;Navigate("pending");if(_pendingFilter.App.Length>0)throw new InvalidOperationException("待分析继承了应用筛选。");
                for(int repeat=0;repeat<4;repeat++){Navigate("hotspot");await Task.Delay(60);Navigate("overview");await Task.Delay(60);}
                var report=new{filtered,all,total=_snapshot.Entries.Count,pendingIndependent=true,hotspotNavigationCycles=4,success=true};
                Directory.CreateDirectory(Path.Combine(DataPaths.UserRoot,"qa"));await File.WriteAllTextAsync(Path.Combine(DataPaths.UserRoot,"qa","filter-regression.json"),Contract.Encode(report));
                AppLog.Write("qa-filter-regression",report);page="entries";_entryFilter.Clear();
            }
            if(page is "tray" or "tray-dark" or "tray-interactive" or "tray-dark-interactive")
            {
                _settings.Theme=page.Contains("dark")?"深色":"浅色";
                var invoked=new TaskCompletionSource<string>();
                var menu=new TrayMenuWindow(TrayState(),command=>{AppLog.Write("qa-tray-invoked",new{command});invoked.TrySetResult(command);});
                try
                {
                    var area=DisplayArea.GetFromWindowId(AppWindow.Id,DisplayAreaFallback.Nearest).WorkArea;menu.ShowAt(area.X+area.Width-30,area.Y+area.Height-30);
                    await menu.CaptureForQa(page);
                    if(page.Contains("interactive"))await Task.WhenAny(invoked.Task,Task.Delay(20000));
                }
                finally{menu.Dismiss();}return;
            }
            if (!new[] { "overview", "entries", "apps", "runtime", "pending", "alerts", "changes", "controls", "groups", "hotspot", "configuration", "settings","codex-origin","dsh-origin" }.Contains(page)) throw new InvalidDataException("未知验证页面。");
            await Task.Delay(1200); if(!retainSelection)Navigate(page); await Task.Delay(700);
            if(Environment.GetCommandLineArgs().Contains("reliability-interactive")&&SettingsWorkspace.Content is ScrollViewer logScroll)
            {logScroll.ChangeView(null,logScroll.ScrollableHeight*0.78,null);await Task.Delay(500);page="settings-logs";}
            // RenderTargetBitmap validates this application's actual XAML tree even on a locked desktop.
            // Native system backdrop visuals are outside this capture and are not claimed as visually verified.
            ShellRoot.Background=new SolidColorBrush(Microsoft.UI.Colors.White);
            var bitmap=new RenderTargetBitmap(); await bitmap.RenderAsync(ShellRoot);
            var pixels=await bitmap.GetPixelsAsync();
            string directory=Path.Combine(AppLog.DataRoot,"qa");Directory.CreateDirectory(directory);
            var folder=await StorageFolder.GetFolderFromPathAsync(directory);
            var file=await folder.CreateFileAsync(page+".png",CreationCollisionOption.ReplaceExisting);
            using var stream=await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,pixels.ToArray());
            await encoder.FlushAsync();
            AppLog.Write("qa-capture-complete",new{page,bitmap.PixelWidth,bitmap.PixelHeight});
        }
        catch(Exception ex){AppLog.Write("qa-capture-failed",new{page,ex.Message});}
        finally{ExitToolbox();}
    }
}
