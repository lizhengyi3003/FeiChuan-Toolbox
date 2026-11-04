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
    private TextBlock Text(string text,double size=0,bool strong=false)
    {
        var block=new TextBlock{Text=text,FontWeight=strong?Microsoft.UI.Text.FontWeights.SemiBold:Microsoft.UI.Text.FontWeights.Normal,TextWrapping=TextWrapping.Wrap};
        if(size>0)block.FontSize=size;else block.SetBinding(TextBlock.FontSizeProperty,new Binding{Source=PageHost,Path=new PropertyPath("FontSize")});
        return block;
    }
    private static StackPanel Column(params UIElement[] elements){var panel=new StackPanel{Spacing=12};foreach(var e in elements)panel.Children.Add(e);return panel;}
    private static Panel Row(params UIElement[] elements){var panel=new FlowPanel();foreach(var e in elements)panel.Children.Add(e);return panel;}
    private static Button Button(string label,Action clicked){var b=new Button{Content=label};b.Click+=(_,_)=>{try{clicked();}catch(Exception ex){if(Application.Current is App app)app.ReportException(ex,label);}};return b;}
    private static Button AsyncButton(string label,Func<Task> clicked){var b=new Button{Content=label};b.Click+=async(_,_)=>{b.IsEnabled=false;try{await clicked();}catch(Exception ex){if(Application.Current is App app)app.ReportException(ex,label);}finally{b.IsEnabled=true;}};return b;}
    private static Border Card(UIElement content)
    {
        var border=(Border)XamlReader.Load("""<Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Padding="18" CornerRadius="10" BorderThickness="1" BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}" Background="{ThemeResource CardBackgroundFillColorDefaultBrush}"/>""");
        border.Child=content;return border;
    }
    private static UIElement Scroll(UIElement content)=>new ScrollViewer{Content=content,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
    private Grid ListPage(string title,UIElement top,UIElement list,UIElement? bottom=null)
    {
        var grid=new Grid{RowSpacing=12};grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
        grid.Children.Add(Column(Text(title,24,true),top));Grid.SetRow((FrameworkElement)list,1);grid.Children.Add(list);if(bottom is not null){Grid.SetRow((FrameworkElement)bottom,2);grid.Children.Add(bottom);}return grid;
    }
}
