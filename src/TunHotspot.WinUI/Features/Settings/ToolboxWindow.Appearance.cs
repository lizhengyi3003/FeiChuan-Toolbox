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
    private void ApplyAppearance()
    {
        try
        {
            var theme=_settings.Theme switch{"浅色"=>ElementTheme.Light,"深色"=>ElementTheme.Dark,_=>ElementTheme.Default};
            ShellRoot.RequestedTheme=theme;Navigation.RequestedTheme=theme;
            Navigation.FontFamily=new FontFamily(_settings.Font);Navigation.FontSize=_settings.FontSize;
            PageHost.FontFamily=new FontFamily(_settings.Font);PageHost.FontSize=_settings.FontSize;
            HotspotWorkspace.FontFamily=new FontFamily(_settings.Font);HotspotWorkspace.FontSize=_settings.FontSize;
            ConfigurationWorkspace.FontFamily=new FontFamily(_settings.Font);ConfigurationWorkspace.FontSize=_settings.FontSize;
            SettingsWorkspace.FontFamily=new FontFamily(_settings.Font);SettingsWorkspace.FontSize=_settings.FontSize;
            bool highContrast=false,effects=true;
            try{highContrast=_accessibility.HighContrast;}catch{}
            try{effects=_uiSettings.AdvancedEffectsEnabled;}catch{}
            bool allow=!highContrast&&effects;
            SystemBackdrop=null;ShellRoot.Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            string material=allow?_settings.Backdrop:"None";
            if(material=="Acrylic"&&DesktopAcrylicController.IsSupported())SystemBackdrop=new DesktopAcrylicBackdrop();
            else if(material is "Mica" or "MicaAlt"&&MicaController.IsSupported())SystemBackdrop=new MicaBackdrop{Kind=material=="MicaAlt"?MicaKind.BaseAlt:MicaKind.Base};
            else {material="None";var systemBackground=_uiSettings.GetColorValue(UIColorType.Background);bool dark=theme==ElementTheme.Dark||(theme==ElementTheme.Default&&systemBackground.R+systemBackground.G+systemBackground.B<384);ShellRoot.Background=new SolidColorBrush(dark?Color.FromArgb(255,32,32,32):Color.FromArgb(255,247,249,252));}
            _materialState=$"选择：{_settings.Backdrop}；当前：{material}。系统高对比度或透明策略优先。";
        }
        catch(Exception ex){SystemBackdrop=null;ShellRoot.Background=new SolidColorBrush(Navigation.ActualTheme==ElementTheme.Dark?Microsoft.UI.Colors.Black:Microsoft.UI.Colors.White);_materialState="材质/字体已回退："+ex.Message;}
    }
    private async Task LoadFonts()
    {
        try{_fonts=await Task.Run(FontCatalog.Installed);AppLog.Write("font-enumeration",new{count=_fonts.Count});}
        catch(Exception ex){_fonts=["Segoe UI Variable","Microsoft YaHei UI"];AppLog.Write("font-enumeration-error",new{ex.Message});}
    }
}
