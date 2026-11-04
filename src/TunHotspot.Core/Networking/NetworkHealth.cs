using Microsoft.Win32;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace TunHotspot.Core;

public sealed class NetworkHealthReport
{
    public DateTimeOffset Time{get;set;}=DateTimeOffset.UtcNow;
    public bool ProxyEnabled{get;set;}
    public bool LoopbackProxy{get;set;}
    public bool ProxyListening{get;set;}
    public bool PacConfigured{get;set;}
    public bool TunConnected{get;set;}
    public bool CanClearStaleProxy=>ProxyEnabled&&LoopbackProxy&&!ProxyListening&&!PacConfigured&&!TunConnected;
    public string Fingerprint{get;set;}="";
    public List<string> Checks{get;set;}=[];
}

public static class NetworkHealth
{
    private const string Settings=@"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    public static async Task<NetworkHealthReport> Read(CancellationToken token=default)
    {
        using var key=Registry.CurrentUser.OpenSubKey(Settings);
        string server=key?.GetValue("ProxyServer") as string??"",pac=key?.GetValue("AutoConfigURL") as string??"";
        bool enabled=Convert.ToInt32(key?.GetValue("ProxyEnable")??0)!=0;
        var report=new NetworkHealthReport{ProxyEnabled=enabled,PacConfigured=pac.Length>0,LoopbackProxy=TryLocal(server,out int port),Fingerprint=Contract.Hash(enabled+"|"+server+"|"+pac),TunConnected=NetworkInterface.GetAllNetworkInterfaces().Any(x=>x.Name.Equals("Kumiryo",StringComparison.OrdinalIgnoreCase)&&x.OperationalStatus==OperationalStatus.Up)};
        if(report.LoopbackProxy){using var socket=new TcpClient();try{using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(1200);await socket.ConnectAsync(IPAddress.Loopback,port,timeout.Token);report.ProxyListening=true;}catch(SocketException){}catch(OperationCanceledException){token.ThrowIfCancellationRequested();}}
        report.Checks.Add($"系统代理：{(enabled?"已启用":"已关闭")}；本地代理：{report.LoopbackProxy}；监听正常：{report.ProxyListening}；PAC：{report.PacConfigured}；Kumiryo TUN：{report.TunConnected}");
        foreach(string host in new[]{"apps.microsoft.com","displaycatalog.mp.microsoft.com","storeedgefd.dsx.mp.microsoft.com"})
        {
            try
            {
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(6));
                var addresses=await Dns.GetHostAddressesAsync(host,timeout.Token);bool fake=addresses.Any(x=>x.AddressFamily==AddressFamily.InterNetwork&&x.GetAddressBytes() is var b&&b[0]==198&&(b[1]==18||b[1]==19));
                using var client=new HttpClient(new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(6)};
                using var response=await client.GetAsync("https://"+host+"/",HttpCompletionOption.ResponseHeadersRead,timeout.Token);
                report.Checks.Add($"{host}：DNS 正常{(fake?"（检测到 TUN Fake-IP）":"")}，不使用系统 HTTP 代理的 HTTPS 请求返回 {(int)response.StatusCode}。");
            }
            catch(Exception ex)when(ex is HttpRequestException or SocketException or OperationCanceledException){token.ThrowIfCancellationRequested();report.Checks.Add(host+"："+DiagnosticLog.Clean(ex.Message));}
        }
        report.Checks.Add(report.CanClearStaleProxy?"检测到指向已停止本地端口的残留代理，可预览后清除启用标志。":"当前没有满足安全清除条件的残留本地代理；不会修改正常代理、PAC 或 TUN。");
        report.Checks.Add("上述请求仍可能经过已开启的 TUN；不能替代代理和 TUN 都关闭时的商店实际加载验收。HTTP 4xx 代表该端点可连接，不代表商店业务全部正常。");
        new DiagnosticLog(DataPaths.UserRoot).Event("网络","信息","Microsoft Store 网络只读检测",string.Join("\n",report.Checks));return report;
    }
    internal static bool TryLocal(string value,out int port)
    {
        port=0;return !value.Contains(';')&&Uri.TryCreate("http://"+value,UriKind.Absolute,out var uri)&&IPAddress.TryParse(uri.Host,out var ip)&&ip.Equals(IPAddress.Loopback)&&(port=uri.Port)>0;
    }
    public static async Task ClearStaleProxy(string expected)
    {
        var report=await Read();if(report.Fingerprint!=expected||!report.CanClearStaleProxy)throw new InvalidOperationException("代理状态已改变或不满足安全清除条件，未修改。");
        using var key=Registry.CurrentUser.OpenSubKey(Settings,true)??throw new IOException("无法打开当前用户代理配置。");
        bool nowEnabled=Convert.ToInt32(key.GetValue("ProxyEnable")??0)!=0;
        string nowServer=key.GetValue("ProxyServer") as string??"",nowPac=key.GetValue("AutoConfigURL") as string??"";
        if(Contract.Hash(nowEnabled+"|"+nowServer+"|"+nowPac)!=expected||NetworkInterface.GetAllNetworkInterfaces().Any(x=>x.Name.Equals("Kumiryo",StringComparison.OrdinalIgnoreCase)&&x.OperationalStatus==OperationalStatus.Up))throw new InvalidOperationException("写入前代理或 TUN 状态已改变，未修改。");
        string folder=Path.Combine(DataPaths.UserRoot,"network-backups");KumiryoPrivateFiles.Directory(folder);
        KumiryoPrivateFiles.Atomic(Path.Combine(folder,DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")+".json"),Contract.Encode(new{ProxyEnable=key.GetValue("ProxyEnable"),ProxyServer=key.GetValue("ProxyServer"),Time=DateTimeOffset.UtcNow}));
        key.SetValue("ProxyEnable",0,RegistryValueKind.DWord);
        if(Convert.ToInt32(key.GetValue("ProxyEnable"))!=0)throw new IOException("代理回读失败，请查看备份。");
        InternetSetOption(0,39,0,0);InternetSetOption(0,37,0,0);
        new DiagnosticLog(DataPaths.UserRoot).Event("网络","信息","已关闭失效本地代理的启用标志","已备份；保留代理地址、绕过列表、PAC、TUN、WinHTTP 与其他网络设置。" );
    }
    [DllImport("wininet.dll",SetLastError=true)]private static extern bool InternetSetOption(nint internet,int option,nint buffer,int length);
}
