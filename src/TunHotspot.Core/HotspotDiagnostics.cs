using System.Net.NetworkInformation;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace TunHotspot.Core;

public static class HotspotDiagnostics
{
    public static object Read()
    {
        var adapters=NetworkInterface.GetAllNetworkInterfaces().ToDictionary(x=>x.Id.Trim('{','}'),StringComparer.OrdinalIgnoreCase);
        var profiles=NetworkInformation.GetConnectionProfiles();var results=new List<object>();
        foreach(var profile in profiles)
        {
            string id=profile.NetworkAdapter?.NetworkAdapterId.ToString()??"";
            string name=adapters.TryGetValue(id,out var adapter)?adapter.Name:"未匹配网卡";
            if(name!="Kumiryo")continue;
            try
            {
                var manager=NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
                results.Add(new{adapter=name,state=manager.TetheringOperationalState.ToString(),clients=manager.ClientCount,
                    devices=manager.GetTetheringClients().Select(x=>new{mac=x.MacAddress,addresses=x.HostNames.Select(h=>h.CanonicalName).ToList()}).ToList()});
            }
            catch(Exception ex){results.Add(new{adapter=name,error=ex.Message});}
        }
        return new{time=DateTimeOffset.Now,networkChanged=false,profiles=results};
    }
}
