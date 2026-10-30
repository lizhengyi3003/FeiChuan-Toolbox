using System.Text.RegularExpressions;

namespace TunHotspot.Core;

public sealed record CreationOriginMark(string EntryId,string Agent,string State,string Evidence,string TargetKey,DateTimeOffset Time);
public sealed record CreationOriginInfo(string Agent,string State,string Evidence)
{
    public bool Related=>State!="无关联"&&State!="已排除";
    public bool Confirmed=>State=="用户确认";
    public string Display=>$"{Agent} · {State} · {Evidence}";
}
public static class CreationOrigins
{
    public static readonly string[] Agents=["Codex","DSH"];
    public static string TargetKey(StartupEntry e)=>Contract.Hash($"{e.Id}|{e.Command}|{e.Executable}|{e.Payload}");
    public static CreationOriginInfo Get(StartupEntry e,string agent)
    {
        var mark=e.CreationMarks.FirstOrDefault(x=>x.Agent==agent);
        if(mark is not null&&mark.TargetKey==TargetKey(e))return new(agent,mark.State,mark.Evidence);
        if(mark is not null)return new(agent,"线索待确认","原人工标记之后目标发生变化，请重新确认。 "+mark.Evidence);
        string text=string.Join("\n",new[]{e.Name,e.Source,e.Command,e.Executable,e.Payload,e.App,e.Evidence.GetValueOrDefault("作者",""),e.Evidence.GetValueOrDefault("描述","")});
        string pattern=agent=="Codex"?@"(?i)(?<![a-z0-9])codex(?![a-z0-9])":@"(?i)((?<![a-z0-9])dsh(?![a-z0-9])|deepseek[ _-]?harness)";
        return Regex.IsMatch(text,pattern)?new(agent,"线索待确认","名称、路径或登记信息包含关联标识；这不能证明创建者，也可能只是该软件自身的项目。 "):new(agent,"无关联","");
    }
}

public sealed class AuditComparison
{
    public DateTimeOffset Time {get;set;}=DateTimeOffset.UtcNow;
    public string FirstId {get;set;}="";
    public string SecondId {get;set;}="";
    public string Privilege {get;set;}="";
    public int Entries {get;set;}
    public List<string> Added {get;set;}=[];
    public List<string> Removed {get;set;}=[];
    public List<string> Changed {get;set;}=[];
    public List<string> Gaps {get;set;}=[];
    public static AuditComparison Between(ScanSnapshot first,ScanSnapshot second)
    {
        var a=first.Entries.ToDictionary(x=>x.Id);var b=second.Entries.ToDictionary(x=>x.Id);
        return new(){FirstId=first.Id,SecondId=second.Id,Privilege=second.Privilege,Time=second.Finished,Entries=b.Count,
            Added=b.Values.Where(x=>!a.ContainsKey(x.Id)).Select(x=>x.Name+" · "+x.Category).ToList(),
            Removed=a.Values.Where(x=>!b.ContainsKey(x.Id)).Select(x=>x.Name+" · "+x.Category).ToList(),
            Changed=b.Values.Where(x=>a.TryGetValue(x.Id,out var old)&&old.Fingerprint!=x.Fingerprint).Select(x=>x.Name+" · "+x.Category).ToList(),
            Gaps=first.Coverage.Concat(second.Coverage).Where(x=>x.Status!="成功").Select(x=>x.Name+"："+x.Status+"；"+x.Note+"；"+string.Join("；",x.Issues)).Distinct().ToList()};
    }
}
