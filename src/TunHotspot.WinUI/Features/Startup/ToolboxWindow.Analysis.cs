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
    private async Task<string?> PickSave(string suggested,string extension)
    {
        var picker=new FileSavePicker{SuggestedFileName=suggested};picker.FileTypeChoices.Add(extension==".html"?"HTML 报告":"JSON 数据",new List<string>{extension});
        WinRT.Interop.InitializeWithWindow.Initialize(picker,WinRT.Interop.WindowNative.GetWindowHandle(this));var file=await picker.PickSaveFileAsync();return file?.Path;
    }
    private async Task ExportBatch()
    {
        var batch=_store.CreateBatch();var preview=new ContentDialog{XamlRoot=ShellRoot.XamlRoot,Title="预览脱敏后的分析证据",PrimaryButtonText="继续导出",CloseButtonText="取消",DefaultButton=ContentDialogButton.Primary,
            Content=new TextBox{Text=Contract.Encode(batch),IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MaxHeight=420,MinWidth=420}};
        if(await preview.ShowAsync()!=ContentDialogResult.Primary)return;
        string? file=await PickSave("TunHotspot-analysis-"+batch.Id[..8],".json");
        if(file is null)return;await File.WriteAllTextAsync(file,Contract.Encode(batch));Tell("分析包已导出","最多包含 100 个已加入待分析区的条目。导出前可在条目详情核对证据。");
    }
    private async Task ImportAnalysis()
    {
        var picker=new FileOpenPicker();picker.FileTypeFilter.Add(".json");WinRT.Interop.InitializeWithWindow.Initialize(picker,WinRT.Interop.WindowNative.GetWindowHandle(this));var file=await picker.PickSingleFileAsync();if(file is null)return;
        if(new FileInfo(file.Path).Length>2_000_000)throw new InvalidDataException("结果文件超出 2 MB 上限。");
        _store.ApplyAnalysis(Contract.Decode<AnalysisResult>(await File.ReadAllTextAsync(file.Path)));Tell("分析结果已保存","只更新建议，系统启动配置未修改。");RenderPage();
    }
    private async Task ExportReport()
    {
        var snapshot=_snapshot??throw new InvalidOperationException("尚无扫描结果。");var file=await PickSave("TunHotspot-startup-"+DateTime.Now.ToString("yyyyMMdd-HHmm"),".html");if(file is null)return;
        static string E(string? text)=>HtmlEncoder.Default.Encode(text??"");
        var html=new StringBuilder("<!doctype html><html lang='zh-CN'><meta charset='utf-8'><title>启动与后台扫描报告</title><style>body{font:15px system-ui;margin:32px;color:#172535}table{border-collapse:collapse;width:100%}td,th{border:1px solid #d7dee5;padding:9px;text-align:left;overflow-wrap:anywhere}tr:nth-child(even){background:#f3f6f9}pre{white-space:pre-wrap}</style><h1>启动与后台只读扫描报告</h1>");
        html.Append($"<p>{E(snapshot.Finished.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))} · {E(snapshot.Privilege)} · {snapshot.Entries.Count} 个登记入口。登记不等于开机常驻。</p><h2>采集覆盖</h2>");
        foreach(var c in snapshot.Coverage)html.Append($"<p><b>{E(c.Display)}</b><br>{E(c.Note)}<br>{E(string.Join("；",c.Issues))}</p>");
        html.Append("<table><tr><th>类别</th><th>条目</th><th>状态</th><th>建议</th><th>理由</th><th>来源 / 命令</th></tr>");
        foreach(var e in snapshot.Entries)html.Append($"<tr><td>{E(e.Category)}</td><td>{E(e.Name)}<br>{E(e.App)}</td><td>{E(e.StateText)}<br>{E(e.Health)}</td><td>{E(e.EffectiveAdvice)}</td><td>{E(e.Analysis?.Reason??e.Reason)}</td><td>{E(e.Source)}<br>{E(e.Command)}</td></tr>");
        html.Append("</table></html>");await File.WriteAllTextAsync(file,html.ToString());Tell("报告已导出","报告包含本机路径和命令参数，请在分享前核对私人信息。");
    }
    private async Task ConnectCodex()
    {
        string? codex=null;var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OpenAI","Codex","bin");
        if(Directory.Exists(root))codex=Directory.EnumerateFiles(root,"codex.exe",SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if(codex is null){Tell("未检测到本机 Codex","扫描不依赖 Codex。可使用导出分析包、导入分析结果，或安装 Codex 后连接。",InfoBarSeverity.Warning);return;}
        if(!File.Exists(AdminClient.EnginePath))throw new FileNotFoundException("分析桥接引擎缺失。");
        var start=new ProcessStartInfo(codex){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{"mcp","add","TunHotspotStartup","--",AdminClient.EnginePath,"--mcp"})start.ArgumentList.Add(arg);
        using var process=Process.Start(start)??throw new IOException("Codex 配置工具未启动。");var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();await process.WaitForExitAsync();
        if(process.ExitCode!=0)throw new IOException(await error);
        Tell("本地分析桥接已注册","按 Codex 的连接提示刷新 MCP 或开启后续对话，再请求分析待分析区。没有读取聊天数据库或登录凭据。");
    }
}
