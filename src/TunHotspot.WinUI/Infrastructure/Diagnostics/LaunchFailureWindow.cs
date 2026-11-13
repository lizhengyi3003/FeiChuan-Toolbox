using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Graphics;
using Windows.Storage;

namespace TunHotspot;

internal sealed class LaunchFailureWindow : Window
{
    private readonly TextBlock _explanation = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _details = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120, MaxHeight = 230 };

    internal LaunchFailureWindow(Func<Task> retry)
    {
        Title = "飞船的小工具箱 · 启动诊断";
        var retryButton = new Button { Content = "重试打开" };
        AutomationProperties.SetAutomationId(retryButton, "LaunchFailure.Retry");
        retryButton.Click += async (_, _) => { retryButton.IsEnabled = false; try { await retry(); } finally { retryButton.IsEnabled = true; } };
        var logs = new Button { Content = "打开诊断目录" };
        logs.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(AppLog.Folder) { UseShellExecute = true }); }
            catch (Exception ex) { _explanation.Text = "诊断目录：" + AppLog.Folder + "\n" + ex.Message; }
        };
        var close = new Button { Content = "关闭" };
        close.Click += (_, _) => Close();
        var panel = new StackPanel { Spacing = 18, Padding = new Thickness(28) };
        panel.Children.Add(new TextBlock { Text = "软件启动遇到问题", FontSize = 24, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_explanation);
        panel.Children.Add(new TextBlock { Text = "可以重试打开，或从诊断目录查看错误记录。原有数据保留在本机。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_details);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        buttons.Children.Add(retryButton); buttons.Children.Add(logs); buttons.Children.Add(close);
        panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"] };
        AutomationProperties.SetAutomationId(_details, "LaunchFailure.Details");
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        int width = Math.Min((int)(700 * scale), area.Width - 30), height = Math.Min((int)(540 * scale), area.Height - 30);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
    }

    internal void SetError(Exception error)
    {
        Exception cause = error;
        while (cause.InnerException is not null) cause = cause.InnerException;
        _explanation.Text = cause is SqliteException sql ? sql.SqliteErrorCode switch
        {
            11 or 26 => "读取本地数据库时发现损坏或格式异常，已停止加载。请保留原文件和诊断记录，以便核查及恢复。",
            5 or 6 => "本地数据库暂时被占用。请稍后重试；如果问题持续，请保留诊断记录。",
            _ => "无法读取本地数据库，请查看下面的错误及诊断记录。"
        } : "初始化没有完成，请查看下面的错误及诊断记录。";
        bool qa=Environment.GetCommandLineArgs().Contains("--qa-capture");
        string root=qa?Path.Combine(Path.GetTempPath(),"TunHotspot-QA",Environment.ProcessId.ToString()):TunHotspot.Core.DataPaths.UserRoot;
        var record=new TunHotspot.Core.DiagnosticLog(root).Record(qa?"程序初始化（隔离测试）":"程序初始化",cause,true);
        _details.Text = $"错误编号：{record.Id}\n{record.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n{cause.GetType().Name}\n{TunHotspot.Core.DiagnosticLog.Clean(cause.Message)}\n{record.Code}\n\n{record.Guidance}\n诊断文件：{record.LogPath}\n{record.StorageWarning}";
    }

    internal async Task CaptureForQa()
    {
        try
        {
            await Task.Delay(700);
            var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync((UIElement)Content);
            var pixels = await bitmap.GetPixelsAsync();
            string directory = Path.Combine(AppLog.DataRoot, "qa"); Directory.CreateDirectory(directory);
            var folder = await StorageFolder.GetFolderFromPathAsync(directory);
            var file = await folder.CreateFileAsync("startup-error.png", CreationCollisionOption.ReplaceExisting);
            using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
            await encoder.FlushAsync();
        }
        catch (Exception ex) { AppLog.Write("qa-startup-capture-failed", new { ex.Message }); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint handle);
}
