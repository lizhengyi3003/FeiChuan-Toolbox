namespace TunHotspot.Core;

public sealed class StartupControlRequest
{
    public string DeviceId { get; set; }=Contract.DeviceId;
    public string EntryId { get; set; }="";
    public string Provider { get; set; }="";
    public string Fingerprint { get; set; }="";
    public string UserNote { get; set; }="";
    public string Operation { get; set; }="";
    public string Value { get; set; }="";
    public string RestoreId { get; set; }="";
}
public sealed class ControlValueBackup
{
    public bool Exists { get; set; }
    public string Binary { get; set; }="";
    public bool Enabled { get; set; }
    public string StartMode { get; set; }="";
    public string Runtime { get; set; }="";
}
public sealed class StartupControlRecord
{
    public string Id { get; set; }=Guid.NewGuid().ToString("N");
    public string DeviceId { get; set; }=Contract.DeviceId;
    public string OwnerSid { get; set; }="";
    public DateTimeOffset Created { get; set; }=DateTimeOffset.UtcNow;
    public string Operation { get; set; }="";
    public string Target { get; set; }="";
    public StartupEntry Before { get; set; }=new();
    public StartupEntry? After { get; set; }
    public ControlValueBackup Backup { get; set; }=new();
    public string Status { get; set; }="已备份，尚未确认执行";
    public string Error { get; set; }="";
    public string RestoreOf { get; set; }="";
    [System.Text.Json.Serialization.JsonIgnore]public string Display=>$"{Created.ToLocalTime():MM-dd HH:mm:ss} · {Before.Name} · {Status}";
    [System.Text.Json.Serialization.JsonIgnore]public string Detail=>(Operation switch
    {"setEnabled"=>Target=="true"?"启用启动入口":"禁用启动入口","serviceStart"=>"启动服务","serviceStop"=>"停止服务","serviceMode"=>"启动类型："+(Target switch{"auto"=>"自动","delayed-auto"=>"自动（延迟）","demand"=>"手动",_=>"禁用"}),_=>"恢复操作"})+(Error.Length>0?" · "+Error:"");
}
public static class ControlPolicy
{
    private static readonly HashSet<string> Critical=new(StringComparer.OrdinalIgnoreCase){"RpcSs","RpcEptMapper","DcomLaunch","LSM","SamSs","PlugPlay","Power","EventLog","BFE","MpsSvc","WinDefend","WdNisSvc","SecurityHealthService","wscsvc","Appinfo","ProfSvc","Schedule","Dhcp","Dnscache","NlaSvc","nsi","WlanSvc","WinHttpAutoProxySvc","SharedAccess","icssvc",DataPaths.ServiceName};
    public static bool RemoteOrProxy(StartupEntry e)=>new[]{"GameViewerService","AnyDesk","ToDesk_Service","SunloginService","KumiryoHelperService","clash_verge_service"}.Contains(e.Name,StringComparer.OrdinalIgnoreCase)||e.Name.Contains("Kumiryo",StringComparison.OrdinalIgnoreCase);
    public static string Reason(StartupEntry entry,string owner)
    {
        if(entry.ObservationStatus!="本次读取")return "这是历史保留结果，需重新扫描确认后才能修改。";
        if(entry.OwnerSid!="machine"&&entry.OwnerSid!=owner)return "其他账户的入口请在对应账户或系统管理界面操作。";
        if(entry.Provider=="services")
        {
            if(entry.Category!="服务"||entry.UserInstance||(int.TryParse(entry.Configuration.GetValueOrDefault("type"),out var serviceType)&&(serviceType&0xC0)!=0))return "驱动、用户服务实例或仅登记项不直接修改；请核对模板或系统工具。";
            if(Critical.Contains(entry.Name))return "关键系统、网络或本软件采集服务受保护，请通过对应系统管理界面处理。";
            return "";
        }
        if(entry.Provider=="tasks")return entry.Source.StartsWith(@"\Microsoft\Windows\",StringComparison.OrdinalIgnoreCase)?"Windows 系统任务需在任务计划程序中核对，不直接修改。":"";
        if(entry.Provider=="run"&&!entry.OneShot&&(entry.Category=="启动文件夹"||entry.Category=="普通登录启动"&&entry.Source.EndsWith(@"\SOFTWARE\Microsoft\Windows\CurrentVersion\Run",StringComparison.OrdinalIgnoreCase)))return "";
        return "此类型尚无受支持的跨应用修改接口，请打开系统配置；扫描和监测仍可使用。";
    }
    public static void Validate(StartupEntry entry,string owner,string operation,string value)
    {
        string reason=Reason(entry,owner);if(reason.Length>0)throw new InvalidOperationException(reason);
        if(operation=="setEnabled"&&(entry.Provider is "run" or "tasks")&&(value is "true" or "false"))return;
        if(entry.Provider=="services")
        {
            if(RemoteOrProxy(entry)&&!(operation=="serviceStart"||operation=="serviceMode"&&(value is "auto" or "delayed-auto")))throw new InvalidOperationException("此服务关联远控或代理连接，当前版本不停止或降低其启动级别，以免断开恢复通道。");
            if((operation=="serviceMode"&&(value is "auto" or "delayed-auto" or "demand" or "disabled"))||(operation is "serviceStart" or "serviceStop"))return;
        }
        throw new InvalidDataException("此操作与条目类型不匹配。");
    }
}
