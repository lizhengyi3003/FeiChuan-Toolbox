using TunHotspot.Core;
namespace TunHotspot;
internal sealed class HotspotService:IDisposable
{
    private readonly HotspotWorkerClient _worker=new(AdminClient.EnginePath,AppLog.DataRoot);
    internal Task<HotspotSnapshot> ReadState()=>_worker.Send("read");
    internal async Task StartAsync()=>await _worker.Send("start");
    internal async Task StopAsync()=>await _worker.Send("stop");
    internal async Task KeepConnectedAsync()=>await _worker.Send("keepalive");
    public void Dispose()=>_worker.Dispose();
}
