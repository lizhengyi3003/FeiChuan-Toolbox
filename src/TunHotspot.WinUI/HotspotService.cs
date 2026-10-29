using System.Net.NetworkInformation;
using Windows.Foundation;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace TunHotspot;

internal sealed record HotspotSnapshot(bool TunConnected, string State, uint Clients, string? Error = null)
{
    internal bool IsOn => State == "On";
    internal bool IsTransitioning => State == "InTransition";
}

internal sealed class HotspotService
{
    private NetworkOperatorTetheringManager? _manager;
    private Guid? _adapterId;
    private IAsyncOperation<NetworkOperatorTetheringOperationResult>? _pendingStart;

    internal HotspotSnapshot ReadState()
    {
        var tun = FindTun();
        try
        {
            var manager = tun is not null ? GetTunManager(tun) : GetStopManager();
            return new(tun is not null, manager.TetheringOperationalState.ToString(), manager.ClientCount);
        }
        catch (Exception)
        {
            return new(tun is not null, "Unknown", 0, tun is null
                ? "请先在 Kumiryo 中开启 TUN。" : "暂时无法读取热点状态，请稍后重试。");
        }
    }

    internal async Task StartAsync()
    {
        var tun = FindTun() ?? throw new InvalidOperationException("请先在 Kumiryo 中开启 TUN，再开启热点。");
        var manager = GetTunManager(tun);
        if (manager.TetheringOperationalState == TetheringOperationalState.On)
            throw new InvalidOperationException("热点已经开启。需要重新连接时，请先手动关闭热点。");
        if (manager.TetheringOperationalState == TetheringOperationalState.InTransition)
            throw new InvalidOperationException("热点正在切换，请稍等几秒再试。");

        // This method is called only by the explicit Start button handler.
        var operation = manager.StartTetheringAsync();
        _pendingStart = operation;
        try { EnsureSuccess(await operation); }
        finally { if (ReferenceEquals(_pendingStart, operation)) _pendingStart = null; }
    }

    internal async Task StopAsync()
    {
        // Cancel/stop is likewise initiated only by the user's Stop button.
        _pendingStart?.Cancel();
        var manager = GetStopManager();
        EnsureSuccess(await manager.StopTetheringAsync());
    }

    private static NetworkInterface? FindTun() => NetworkInterface.GetAllNetworkInterfaces()
        .FirstOrDefault(adapter => adapter.Name.Equals("Kumiryo", StringComparison.OrdinalIgnoreCase)
            && adapter.OperationalStatus == OperationalStatus.Up);

    private NetworkOperatorTetheringManager GetTunManager(NetworkInterface tun)
    {
        if (!Guid.TryParse(tun.Id, out var id)) throw new InvalidOperationException("无法识别 TUN 网卡。");
        if (_manager is not null && _adapterId == id) return _manager;
        var profiles = NetworkInformation.GetConnectionProfiles()
            .Where(profile => profile.NetworkAdapter?.NetworkAdapterId == id).ToArray();
        var profile = profiles.FirstOrDefault(p => p.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess)
            ?? profiles.FirstOrDefault()
            ?? throw new InvalidOperationException("TUN 网络尚未准备好，请稍等几秒再试。");
        _manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
        _adapterId = id;
        return _manager;
    }

    private NetworkOperatorTetheringManager GetStopManager()
    {
        if (_manager is not null) return _manager;
        var tun = FindTun();
        if (tun is not null) return GetTunManager(tun);
        var profile = NetworkInformation.GetInternetConnectionProfile()
            ?? throw new InvalidOperationException("请从 Windows 快捷设置中手动关闭热点。");
        return NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
    }

    private static void EnsureSuccess(NetworkOperatorTetheringOperationResult result)
    {
        if (result.Status == TetheringOperationStatus.Success) return;
        var code = result.Status.ToString();
        AppLog.Write("windows-operation-result", new { status = code });
        var message = code switch
        {
            "OperationInProgress" => "Windows 正在切换热点，请稍后再点一次。",
            "WiFiDeviceOff" => "请先在 Windows 中开启 Wi-Fi。",
            "NetworkLimitedConnectivity" => "当前网络连接受限，请先确认电脑可以上网。",
            _ => $"Windows 未完成热点操作（{code}）。可手动关闭热点后重试。"
        };
        throw new InvalidOperationException(message);
    }
}
