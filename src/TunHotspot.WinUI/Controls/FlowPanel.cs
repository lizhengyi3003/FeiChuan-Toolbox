using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace TunHotspot;

internal sealed class FlowPanel : Panel
{
    private const double Gap=8;
    protected override Size MeasureOverride(Size availableSize)
    {
        double width=0,lineWidth=0,height=0,lineHeight=0;
        foreach(var child in Children)
        {
            child.Measure(new Size(availableSize.Width,double.PositiveInfinity));var desired=child.DesiredSize;
            if(lineWidth>0&&lineWidth+desired.Width>availableSize.Width){width=Math.Max(width,lineWidth-Gap);height+=lineHeight+Gap;lineWidth=0;lineHeight=0;}
            lineWidth+=desired.Width+Gap;lineHeight=Math.Max(lineHeight,desired.Height);
        }
        return new Size(Math.Max(width,Math.Max(0,lineWidth-Gap)),height+lineHeight);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        double x=0,y=0,lineHeight=0;
        foreach(var child in Children)
        {
            var desired=child.DesiredSize;
            if(x>0&&x+desired.Width>finalSize.Width){y+=lineHeight+Gap;x=0;lineHeight=0;}
            child.Arrange(new Rect(x,y,Math.Min(desired.Width,finalSize.Width),desired.Height));x+=desired.Width+Gap;lineHeight=Math.Max(lineHeight,desired.Height);
        }
        return finalSize;
    }
}
