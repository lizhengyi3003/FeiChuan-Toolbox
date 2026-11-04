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
    internal void OpenEvent(string id)
    {
        if(_startupUnavailable){Show();Navigate("settings");return;}
        var item=_store.Alerts.FirstOrDefault(x=>x.Id==id);if(item is not null){item.IsRead=true;_store.SaveAlert(item);}Show();Navigate("alerts");
    }
    private void Cleanup()
    {
        if(!_qa)try{_preferences.Save(_settings);}catch(Exception ex){AppLog.Write("preferences-save-failed",new{ex.Message});}
        _poll.Stop();_hotspot.Dispose();_configuration?.Dispose();_adminStop?.Cancel();_trayMenu?.Dismiss();_tray?.Dispose();NotificationBridge.Close();_monitor.Dispose();
        if(!_qa&&!_startupUnavailable)try{_store.CreateVerifiedBackup();}catch(Exception ex){_diagnostics.Record("关闭前数据库备份",ex);}
        AppLog.Write("toolbox-closed",new {networkChanged=false,machineMonitorMayContinue=_serviceConnected});
    }
    private void ExitToolbox(){_exiting=true;Close();}
    private void Tell(string title,string body,InfoBarSeverity severity=InfoBarSeverity.Informational)
    {Message.Title=title;Message.Message=body;Message.Severity=severity;Message.IsOpen=true;AppLog.Write(severity==InfoBarSeverity.Error?"ui-operation-error":severity==InfoBarSeverity.Warning?"ui-operation-warning":"ui-notice",new{title,body});}
    [System.Runtime.InteropServices.DllImport("user32.dll")]private static extern uint GetDpiForWindow(nint hwnd);
}
