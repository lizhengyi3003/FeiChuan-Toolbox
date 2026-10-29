using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using TunHotspot.Core;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.System;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace TunHotspot;

internal sealed record TrayMenuState(UserSettings Settings,bool MachineConnected,bool ScanBusy,int Unread);

// XAML owns every visible menu element. Win32 is limited to shell integration and window placement/chrome.
internal sealed class TrayMenuWindow : Window
{
    private readonly Grid _root=new();
    private readonly Border _surface;
    private readonly StackPanel _items=new(){Spacing=2};
    private readonly List<Button> _buttons=[];
    private readonly TrayMenuState _state;
    private readonly Action<string> _command;
    private readonly bool _qa;
    private bool _shown,_closed;
    private int _x,_y;

    internal TrayMenuWindow(TrayMenuState state,Action<string> command,bool qa=false)
    {
        _state=state;_command=command;_qa=qa;Title="TunHotspot 托盘菜单";
        ExtendsContentIntoTitleBar=true;
        AppWindow.IsShownInSwitchers=false;
        var presenter=(OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false,false);presenter.IsResizable=false;presenter.IsMinimizable=false;presenter.IsMaximizable=false;
        presenter.IsAlwaysOnTop=!qa;
        _surface=(Border)XamlReader.Load("""
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                CornerRadius="8" Padding="6" BorderThickness="1"
                Background="{ThemeResource CardBackgroundFillColorDefaultBrush}"
                BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}"/>
            """);
        var body=new StackPanel{Spacing=4};
        body.Children.Add(new TextBlock{Text="TUN 热点与启动工具箱",FontFamily=new FontFamily(state.Settings.Font),FontWeight=Microsoft.UI.Text.FontWeights.SemiBold,Margin=new Thickness(12,9,12,0),FontSize=14});
        body.Children.Add(new TextBlock{Text=$"{(state.Settings.Paused?"监测已暂停":"只读监测")}{(state.Unread>0?$" · {state.Unread} 条未读提醒":"")}",FontFamily=new FontFamily(state.Settings.Font),Opacity=0.65,FontSize=12,Margin=new Thickness(12,0,12,7)});
        Add("打开工具箱","\uE80F","open","O");
        Add("全局扫描（管理员）","\uE721","scan","G",!state.ScanBusy);
        Add("通知与关注","\uE7F4","alerts","N");
        Add(state.Settings.Paused?"恢复监测":"暂停监测",state.Settings.Paused?"\uE768":"\uE769","pause","P");
        Add("设置","\uE713","settings","S");
        body.Children.Add(_items);
        body.Children.Add((UIElement)XamlReader.Load("""
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Height="1" Margin="10,5" Background="{ThemeResource CardStrokeColorDefaultBrush}"/>
            """));
        var exit=CreateItem("退出托盘","\uE8BB","exit","Q",true);body.Children.Add(exit);
        body.Children.Add(new TextBlock{Text=state.MachineConnected?"退出后，机器采集组件继续运行":"退出后，当前会话监测停止",FontFamily=new FontFamily(state.Settings.Font),FontSize=11,Opacity=0.65,Margin=new Thickness(12,0,12,7),TextWrapping=TextWrapping.Wrap});
        _surface.Child=new ScrollViewer{Content=body,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        _root.Children.Add(_surface);Content=_root;
        ApplyAppearance();
        _root.KeyDown+=KeyDown;
        _root.Loaded+=(_,_)=>{Fit();_buttons[0].Focus(FocusState.Programmatic);};
        Activated+=(_,e)=>{if(_shown&&!_qa&&e.WindowActivationState==WindowActivationState.Deactivated)Dismiss();};
        Closed+=(_,_)=>_closed=true;
        var handle=WinRT.Interop.WindowNative.GetWindowHandle(this);
        int round=2;_ = DwmSetWindowAttribute(handle,33,ref round,sizeof(int));
    }
    private void Add(string text,string glyph,string command,string access,bool enabled=true)=>_items.Children.Add(CreateItem(text,glyph,command,access,enabled));
    private Button CreateItem(string text,string glyph,string command,string access,bool enabled)
    {
        var content=new Grid{ColumnSpacing=12};content.ColumnDefinitions.Add(new(){Width=new GridLength(20)});content.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        content.Children.Add(new FontIcon{Glyph=glyph,FontFamily=new FontFamily("Segoe Fluent Icons"),FontSize=17,VerticalAlignment=VerticalAlignment.Center});
        var label=new TextBlock{Text=text,FontFamily=new FontFamily(_state.Settings.Font),VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis,FontSize=_state.Settings.FontSize};Grid.SetColumn(label,1);content.Children.Add(label);
        var button=new Button{Content=content,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch,
            Padding=new Thickness(12,9,12,9),Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent),BorderThickness=new Thickness(0),CornerRadius=new CornerRadius(5),IsEnabled=enabled,AccessKey=access};
        AutomationProperties.SetName(button,text);AutomationProperties.SetAutomationId(button,"TrayMenu."+command);
        button.Click+=(_,_)=>{Dismiss();_command(command);};_buttons.Add(button);return button;
    }
    private void ApplyAppearance()
    {
        var settings=_state.Settings;var system=new UISettings();bool highContrast=false,effects=true;
        try{highContrast=new AccessibilitySettings().HighContrast;effects=system.AdvancedEffectsEnabled;}catch{}
        _root.RequestedTheme=settings.Theme switch{"浅色"=>ElementTheme.Light,"深色"=>ElementTheme.Dark,_=>ElementTheme.Default};
        _surface.RequestedTheme=_root.RequestedTheme;
        foreach(var button in _buttons)button.FontFamily=new FontFamily(settings.Font);
        string material=highContrast||!effects||_qa?"None":settings.Backdrop;
        try
        {
            if(material=="Acrylic"&&DesktopAcrylicController.IsSupported())SystemBackdrop=new DesktopAcrylicBackdrop();
            else if(material is "Mica" or "MicaAlt"&&MicaController.IsSupported())SystemBackdrop=new MicaBackdrop{Kind=material=="MicaAlt"?MicaKind.BaseAlt:MicaKind.Base};
        }
        catch(Exception ex){SystemBackdrop=null;AppLog.Write("tray-material-fallback",new{ex.Message});}
        var background=system.GetColorValue(UIColorType.Background);
        bool dark=settings.Theme=="深色"||(settings.Theme=="跟随系统"&&background.R+background.G+background.B<384);
        _root.Background=SystemBackdrop is not null?new SolidColorBrush(Microsoft.UI.Colors.Transparent):new SolidColorBrush(highContrast?background:dark?Color.FromArgb(255,32,32,32):Color.FromArgb(255,249,249,249));
    }
    internal void ShowAt(int x,int y)
    {
        _x=x;_y=y;AppWindow.Move(new PointInt32(x,y));Fit();_shown=true;Activate();
        AppLog.Write("tray-menu-opened",new{framework="WinUI 3",_state.Settings.Theme,_state.Settings.Backdrop});
    }
    private void Fit()
    {
        var area=DisplayArea.GetFromRect(new RectInt32(_x,_y,1,1),DisplayAreaFallback.Nearest).WorkArea;
        double scale=GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this))/96.0;
        double width=Math.Min(320+Math.Max(0,_state.Settings.FontSize-14)*8,(area.Width-16)/scale);
        _root.Measure(new Size(width,double.PositiveInfinity));
        int pixelWidth=(int)Math.Ceiling(width*scale),pixelHeight=Math.Min((int)Math.Ceiling(_root.DesiredSize.Height*scale),area.Height-16);
        pixelHeight=Math.Max(160,pixelHeight);
        AppWindow.MoveAndResize(new RectInt32(Math.Clamp(_x-pixelWidth,area.X+8,Math.Max(area.X+8,area.X+area.Width-pixelWidth-8)),Math.Clamp(_y-pixelHeight,area.Y+8,Math.Max(area.Y+8,area.Y+area.Height-pixelHeight-8)),pixelWidth,pixelHeight));
    }
    private void KeyDown(object sender,KeyRoutedEventArgs e)
    {
        if(e.Key==VirtualKey.Escape){Dismiss();e.Handled=true;return;}
        if(e.Key is not (VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End))return;
        var enabled=_buttons.Where(x=>x.IsEnabled).ToList();if(enabled.Count==0)return;
        object focused=FocusManager.GetFocusedElement(_root.XamlRoot);int index=enabled.FindIndex(x=>ReferenceEquals(x,focused));
        index=e.Key switch{VirtualKey.Home=>0,VirtualKey.End=>enabled.Count-1,VirtualKey.Up=>(index-1+enabled.Count)%enabled.Count,_=>(index+1)%enabled.Count};
        enabled[index].Focus(FocusState.Keyboard);e.Handled=true;
    }
    internal void Dismiss(){if(!_closed){_closed=true;Close();}}
    internal async Task CaptureForQa(string name)
    {
        await Task.Delay(500);Fit();await Task.Delay(150);
        var bitmap=new RenderTargetBitmap();await bitmap.RenderAsync(_root);var pixels=await bitmap.GetPixelsAsync();
        string directory=Path.Combine(DataPaths.UserRoot,"qa");Directory.CreateDirectory(directory);var folder=await StorageFolder.GetFolderFromPathAsync(directory);
        var file=await folder.CreateFileAsync(name+".png",CreationCollisionOption.ReplaceExisting);using var stream=await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,pixels.ToArray());await encoder.FlushAsync();
        AppLog.Write("qa-tray-rendered",new{name,bitmap.PixelWidth,bitmap.PixelHeight,controls=_buttons.Count});
    }
    [DllImport("user32.dll")]private static extern uint GetDpiForWindow(nint window);
    [DllImport("dwmapi.dll")]private static extern int DwmSetWindowAttribute(nint window,uint attribute,ref int value,int size);
}
