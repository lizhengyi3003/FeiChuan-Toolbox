using System.ServiceProcess;

namespace TunHotspot.Engine;

// Disposable Windows Sandbox fixture; never selected by normal application or service startup.
internal sealed class ControlFixtureService:ServiceBase
{
    internal ControlFixtureService(){ServiceName="TunHotspot.Qa.Control";CanStop=true;AutoLog=false;}
}
