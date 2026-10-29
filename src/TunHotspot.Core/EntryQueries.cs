namespace TunHotspot.Core;

public sealed class EntryFilter
{
    public string Search { get; set; }="";
    public string Category { get; set; }="";
    public string Advice { get; set; }="";
    public string Control { get; set; }="";
    public string Runtime { get; set; }="";
    public string Owner { get; set; }="";
    public string App { get; set; }="";
    public bool Active=>new[]{Search,Category,Advice,Control,Runtime,Owner,App}.Any(x=>x.Length>0);
    public void Clear(){Search=Category=Advice=Control=Runtime=Owner=App="";}
    public List<StartupEntry> Apply(IEnumerable<StartupEntry> entries,IReadOnlySet<string>? pending=null)
    {
        string query=Search.Trim();
        return entries.Where(e=>(pending is null||pending.Contains(e.Id))&&(Category.Length==0||e.Category==Category)&&
            (Advice.Length==0||e.EffectiveAdvice==Advice)&&(Control.Length==0||e.ControlValue==Control)&&
            (Runtime.Length==0||EntryQueries.Runtime(e)==Runtime)&&(Owner.Length==0||e.OwnerSid==Owner)&&(App.Length==0||e.App==App)&&
            (query.Length==0||$"{e.Name} {e.App} {e.Command} {e.Source} {e.Publisher} {e.UserNote}".Contains(query,StringComparison.CurrentCultureIgnoreCase)))
            .OrderBy(e=>e.Category).ThenBy(e=>e.Name).ToList();
    }
}
public sealed record EntryGroup(string Name,int Count,string Kind)
{public string Display=>$"{Name} · {Count:N0} 项";public string Detail=>$"按{Kind}分组；选择查看条目";}
public static class EntryQueries
{
    public static string Runtime(StartupEntry e)=>e.State is "Running" or "运行中"?"正在运行":e.State is "Stopped" or "禁用" or "就绪"?"未运行":"运行状态未确认";
    public static IEnumerable<EntryGroup> Groups(IEnumerable<StartupEntry> entries,string kind)=>entries.GroupBy(e=>Key(e,kind)).Select(g=>new EntryGroup(g.Key,g.Count(),kind)).OrderByDescending(x=>x.Count).ThenBy(x=>x.Name);
    public static string Key(StartupEntry e,string kind)=>kind switch{"类别"=>e.Category,"建议"=>e.EffectiveAdvice,"启用状态"=>e.ControlValue,"运行状态"=>Runtime(e),"账户"=>e.OwnerSid,_=>e.App};
    public static void SelectGroup(EntryFilter filter,string kind,string name)
    {filter.Clear();switch(kind){case "类别":filter.Category=name;break;case "建议":filter.Advice=name;break;case "启用状态":filter.Control=name;break;case "运行状态":filter.Runtime=name;break;case "账户":filter.Owner=name;break;default:filter.App=name;break;}}
}
