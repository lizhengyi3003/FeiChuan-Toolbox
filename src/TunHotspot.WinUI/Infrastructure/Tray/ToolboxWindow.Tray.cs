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
    private TrayMenuState TrayState()=>new(_settings,_serviceConnected,_adminBusy||_serviceScanRequested||_startupUnavailable,_startupUnavailable?0:_store.Alerts.Count(x=>!x.IsRead),!_startupUnavailable);
    private void OpenTrayMenu(int x,int y)
    {
        try
        {
            if(_trayMenu is not null){_trayMenu.Activate();return;}
            var menu=new TrayMenuWindow(TrayState(),HandleTrayCommand);_trayMenu=menu;
            menu.Closed+=(_,_)=>{if(ReferenceEquals(_trayMenu,menu))_trayMenu=null;};menu.ShowAt(x,y);
        }
        catch(Exception ex){_trayMenu?.Dismiss();_trayMenu=null;ReportError(ex.Message);AppLog.Write("tray-menu-error",new{ex.Message});}
    }
    private async void HandleTrayCommand(string command)
    {
        try
        {
            if (_startupUnavailable && command is "scan" or "pause") { Show(); Navigate("overview"); return; }
            if(command=="open")Show();else if(command is "alerts" or "settings"){Show();Navigate(command);}
            else if(command=="hotspot"){Show();Navigate("hotspot");}
            else if(command=="configuration"){Show();Navigate("configuration");}
            else if(command=="scan"){Show();Navigate("overview");await GlobalScan();}else if(command=="pause")await Pause();else if(command=="exit")ExitToolbox();
        }
        catch(Exception ex){ReportError(ex.Message);}
    }
}
