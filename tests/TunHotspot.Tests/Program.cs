using TunHotspot.Core;

if(args.Length==1&&args[0]=="--installer-exit-fixture")
{
    using var watch=SetupGate.WatchForExit(()=>Environment.Exit(0));
    Console.WriteLine("ready");await Console.Out.FlushAsync();await Task.Delay(Timeout.Infinite);return;
}
if(args.Length>0&&args[0]=="--hotspot-worker")
{
    int reads=0;while(await Console.In.ReadLineAsync() is string command){if(++reads>1)Environment.Exit(37);Console.WriteLine(Contract.Encode(new HotspotReply(true,new(false,"Unavailable",0,"fixture"))));await Console.Out.FlushAsync();}return;
}
if(args.Length==2&&args[0]=="--crash-database")
{
    var crashStore=new ToolboxStore(args[1]);crashStore.Set("committed",10);
    using var db=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{DataSource=Path.Combine(crashStore.Root,"toolbox.db"),Pooling=false}.ToString());db.Open();
    using(var config=db.CreateCommand()){config.CommandText="PRAGMA cache_size=5; PRAGMA cache_spill=ON; PRAGMA synchronous=FULL";config.ExecuteNonQuery();}
    using var transaction=db.BeginTransaction();using(var write=db.CreateCommand()){write.Transaction=transaction;write.CommandText="CREATE TABLE interrupted(id INTEGER PRIMARY KEY,data BLOB)";write.ExecuteNonQuery();write.CommandText="INSERT INTO interrupted(data) VALUES(zeroblob(8192))";for(int i=0;i<100;i++)write.ExecuteNonQuery();}
    Console.WriteLine("committed");await Console.Out.FlushAsync();await Task.Delay(Timeout.Infinite);
}
if(args.Length==2&&args[0]=="--verify-database"){_=new ToolboxStore(args[1],true);Console.WriteLine("ok");return;}

int passed=0;
void Check(bool condition,string message){if(!condition)throw new Exception(message);passed++;Console.WriteLine("PASS "+message);}
void Rejected(Action action,string message){try{action();}catch(Exception ex)when(ex is InvalidDataException or System.Text.Json.JsonException){Check(true,message);return;}throw new Exception("未拒绝："+message);}
var root=Path.Combine(Path.GetTempPath(),"TunHotspot-tests-"+Guid.NewGuid().ToString("N"));
try
{
 var installRoot=Path.Combine(root,"installed");Directory.CreateDirectory(Path.Combine(installRoot,"Engine"));
 string uiFixture=Path.Combine(installRoot,"TunHotspot.exe"),engineFixture=Path.Combine(installRoot,"Engine","TunHotspot.Engine.exe");
 Check(SetupGate.InstallRoot(uiFixture)==installRoot&&SetupGate.InstallRoot(engineFixture)==installRoot,"界面与 Engine 使用同一安装锁目录");
 Check(!SetupGate.IsBlocked(uiFixture),"无安装锁时不阻止正常启动");
 File.WriteAllText(Path.Combine(installRoot,SetupGate.MarkerName),"{}");
 Check(SetupGate.IsBlocked(uiFixture)&&SetupGate.IsBlocked(engineFixture),"安装中或遗留锁同时阻止界面和后台引擎启动");
 File.Delete(Path.Combine(installRoot,SetupGate.MarkerName));
 var exitStart=new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true};
 exitStart.ArgumentList.Add("--installer-exit-fixture");using(var child=System.Diagnostics.Process.Start(exitStart)!)
 {
  Check(await child.StandardOutput.ReadLineAsync()=="ready","专用安装退出通知已注册");
  using var signal=EventWaitHandle.OpenExisting(SetupGate.ExitEventName(child.Id,child.StartTime.ToUniversalTime().Ticks));signal.Set();
  Check(child.WaitForExit(5000)&&child.ExitCode==0,"安装退出请求只结束已核实 PID 和启动时刻的工作进程");
 }
 var recovery=new HotspotRecoveryPolicy();var clock=DateTimeOffset.UtcNow;
 Check(!recovery.Observe(clock,true,"Off"),"观察已有热点不自动接管或启动");
 recovery.SetRequested(true);
 Check(!recovery.Observe(clock,true,"Off")&&!recovery.Observe(clock.AddSeconds(11),true,"Off"),"热点断开先等待稳定状态");
 Check(recovery.Observe(clock.AddSeconds(12),true,"Off"),"明确保持连接后才允许重连");
 recovery.AttemptStarted(clock.AddSeconds(12));
 Check(!recovery.Observe(clock.AddSeconds(13),true,"Off")&&!recovery.Observe(clock.AddSeconds(26),true,"Off"),"重连失败后的退避抑制频繁开关");
 Check(recovery.Observe(clock.AddSeconds(27),true,"Off"),"退避完成后允许下一次重连");
 recovery.AttemptStarted(clock.AddSeconds(27));recovery.Observe(clock.AddSeconds(28),true,"On");recovery.Observe(clock.AddSeconds(29),true,"Off");
 Check(recovery.Attempts==2,"短暂恢复不清空重试预算");
 recovery.AttemptStarted(clock.AddSeconds(60));
 Check(recovery.Exhausted&&!recovery.Observe(clock.AddMinutes(5),true,"Off"),"三次失败停止重试而非无限循环");
 recovery.SetRequested(true);recovery.Observe(clock,true,"Off");
 Check(!recovery.Observe(clock.AddMinutes(1),false,"Off")&&!recovery.Observe(clock.AddMinutes(2),true,"Unknown")&&!recovery.Observe(clock.AddMinutes(3),true,"InTransition"),"TUN 不在或状态不明时不切换到其他网络");
 recovery.SetRequested(false);
 Check(!recovery.Observe(clock.AddMinutes(10),true,"Off"),"手动关闭撤销重连意图");
 recovery.SetRequested(true);recovery.AttemptStarted(clock);recovery.Observe(clock.AddSeconds(1),true,"On");recovery.Observe(clock.AddSeconds(61),true,"On");
 Check(recovery.Attempts==0,"持续稳定一分钟才恢复重试预算");
 var store=new ToolboxStore(root);
 var migrationSource=Path.Combine(root,"migration-source");var migrationTarget=Path.Combine(root,"migration-target");var originalStore=new ToolboxStore(migrationSource);originalStore.Set("migrationMarker","preserved");
 UserDataMigration.Ensure(migrationTarget,migrationSource);Check(new ToolboxStore(migrationTarget,true).Get<string>("migrationMarker")=="preserved"&&File.Exists(Path.Combine(migrationSource,"toolbox.db")),"在线迁移核验副本且保留旧数据库");
 var badSource=Path.Combine(root,"bad-migration");Directory.CreateDirectory(badSource);File.WriteAllText(Path.Combine(badSource,"toolbox.db"),"broken");var badTarget=Path.Combine(root,"bad-target");
 try{UserDataMigration.Ensure(badTarget,badSource);throw new Exception("bad migration accepted");}catch(Exception ex)when(ex is Microsoft.Data.Sqlite.SqliteException or InvalidDataException){Check(!File.Exists(Path.Combine(badTarget,"toolbox.db")),"损坏旧库不会被当成空库迁移");}
 string verifiedBackup=store.CreateVerifiedBackup();using(var backupDb=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+verifiedBackup)){backupDb.Open();using var check=backupDb.CreateCommand();check.CommandText="PRAGMA integrity_check";Check(check.ExecuteScalar() as string=="ok","在线备份通过完整性核验");}
 var restoreRoot=Path.Combine(root,"restore-fixture");var restoreStore=new ToolboxStore(restoreRoot);restoreStore.Set("restoreMarker","before");restoreStore.CreateVerifiedBackup();File.WriteAllText(Path.Combine(restoreRoot,"toolbox.db"),"damaged fixture");string originalArchive=DatabaseRecovery.RestoreLatest(restoreRoot);
 Check(new ToolboxStore(restoreRoot,true).Get<string>("restoreMarker")=="before"&&File.ReadAllText(Path.Combine(originalArchive,"toolbox.db"))=="damaged fixture","恢复有效备份并完整保留损坏原件");
 Check(NetworkHealth.TryLocal("127.0.0.1:7890",out int proxyPort)&&proxyPort==7890&&!NetworkHealth.TryLocal("127.0.0.2:7890",out _)&&!NetworkHealth.TryLocal("[::1]:7890",out _)&&!NetworkHealth.TryLocal("http=proxy.example:8080;https=proxy.example:8080",out _),"残留代理修复仅接受实际探测的 IPv4 本机地址，不扩大到其他代理");
 for(int cycle=0;cycle<3;cycle++)
 {
    string crashRoot=Path.Combine(root,"crash-cycle-"+cycle);var start=new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true};start.ArgumentList.Add("--crash-database");start.ArgumentList.Add(crashRoot);
    using var child=System.Diagnostics.Process.Start(start)!;using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));Check(await child.StandardOutput.ReadLineAsync(deadline.Token)=="committed","强制退出前提交屏障 "+cycle);child.Kill();await child.WaitForExitAsync();
    if(cycle==1)
    {
        var readers=new List<System.Diagnostics.Process>();for(int reader=0;reader<2;reader++){var readStart=new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true};readStart.ArgumentList.Add("--verify-database");readStart.ArgumentList.Add(crashRoot);readers.Add(System.Diagnostics.Process.Start(readStart)!);}
        foreach(var reader in readers){await reader.WaitForExitAsync();Check(reader.ExitCode==0&&reader.StandardOutput.ReadToEnd().Trim()=="ok","两个进程并发启动时协调热日志恢复");reader.Dispose();}
    }
    Check(new ToolboxStore(crashRoot,true).Get<int>("committed")>=10,"写入中强制退出后保留已提交数据且完整性正常 "+cycle);
    Check(Directory.Exists(Path.Combine(crashRoot,"transaction-recovery"))&&Directory.GetFiles(Path.Combine(crashRoot,"transaction-recovery"),"toolbox.db-journal",SearchOption.AllDirectories).Length==1,"恢复热回滚日志前成套保留原件 "+cycle);
 }
 var startupFixture=@"Software\TunHotspot.Tests\"+Guid.NewGuid().ToString("N");
 try
 {
    string runKey=startupFixture+@"\Run",approvedKey=startupFixture+@"\Approved";var initial=StartupRegistration.ReadAt(runKey,approvedKey);
    var enabled=StartupRegistration.SetAt(true,"C:\\Fixture\\TunHotspot.exe",initial.Fingerprint,root,runKey,approvedKey);Check(enabled.Enabled,"登录启动配置回读成功（隔离注册表）");
    using(var approval=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(approvedKey,true))approval!.SetValue(StartupRegistration.ValueName,new byte[]{3,0,0,0},Microsoft.Win32.RegistryValueKind.Binary);
    var disabled=StartupRegistration.ReadAt(runKey,approvedKey);Check(disabled.WindowsDisabled&&!disabled.Enabled,"识别任务管理器禁用状态");
    try{StartupRegistration.SetAt(true,"C:\\Fixture\\TunHotspot.exe",enabled.Fingerprint,root,runKey,approvedKey);throw new Exception("stale preview accepted");}catch(InvalidOperationException){Check(true,"拒绝覆盖预览后被其他程序改变的登录启动状态");}
    var removed=StartupRegistration.SetAt(false,"C:\\Fixture\\TunHotspot.exe",disabled.Fingerprint,root,runKey,approvedKey);Check(!removed.Registered,"关闭仅移除本软件的登录启动值");
 }
 finally{Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(startupFixture,false);}
 using(var worker=new HotspotWorkerClient(Environment.ProcessPath!,root))
 {
    Check((await worker.Send("read")).State=="Unavailable","热点隔离进程可报告 TUN 不可用");
    try{await worker.Send("read");throw new Exception("worker exit ignored");}catch(IOException){Check(true,"热点子进程崩溃被捕获，调用进程保持运行");}
 }
 var filteredLog=new DiagnosticLog(Path.Combine(root,"filter-log"));filteredLog.Event("热点","警告","断开","https://user:secret@example.com/?access_token=hidden");filteredLog.Event("设置","信息","保存设置");
 Check(filteredLog.Recent("热点","警告").Count==1&&filteredLog.Recent("设置","错误").Count==0,"日志按类别和严重等级交叉筛选");
 Check(!filteredLog.Recent("热点")[0].Details.Contains("hidden")&&!filteredLog.Recent("热点")[0].Details.Contains("secret"),"日志过滤 URL 凭据和查询令牌");
 var preferencesRoot=Path.Combine(root,"independent-preferences");Directory.CreateDirectory(preferencesRoot);File.WriteAllText(Path.Combine(preferencesRoot,"toolbox.db"),"damaged startup history");
 var preferences=new PreferencesStore(preferencesRoot);preferences.Save(new(){Theme="深色",Backdrop="Acrylic",FontSize=16});
 Check(new PreferencesStore(preferencesRoot).Load() is {Theme:"深色",Backdrop:"Acrylic",FontSize:16},"全局设置可独立于损坏的启动数据库保存与重载");
 preferences.Save(new(){Theme="浅色"});File.WriteAllText(preferences.Path,"damaged preferences");
 Check(preferences.Load() is {Theme:"深色"}&&preferences.Warning.Length>0,"应用设置损坏时读取上一份有效设置并给出提示");
 preferences.Save(new(){Theme="跟随系统"});Check(Directory.GetFiles(preferencesRoot,"preferences.json.damaged-*").Length==1,"保存新设置前保留损坏设置原件");
 using(var guard=new FileStream(ToolboxStore.MaintenanceFile(root),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
 {try{_=store.Settings;throw new Exception("Maintenance guard ignored");}catch(IOException){Check(true,"离线维护持有排他锁时拒绝新数据库连接");}}
 _=store.Settings;using(var guard=new FileStream(ToolboxStore.MaintenanceFile(root),FileMode.Open,FileAccess.ReadWrite,FileShare.None)){Check(true,"数据库操作关闭后释放维护租约和文件连接");}
 var diagnostics=new DiagnosticLog(Path.Combine(root,"diagnostic-tests"));var incident=diagnostics.Record("只读测试",new IOException("password=private-pass Authorization: Bearer private-token --api-key cli-secret"),true);
 var savedIncident=File.ReadAllText(incident.LogPath);Check(incident.Id.Length==12&&incident.ReadRetryAllowed&&!savedIncident.Contains("private-pass")&&!savedIncident.Contains("private-token")&&!savedIncident.Contains("cli-secret")&&diagnostics.Recent().Count==1,"独立诊断包含错误编号及重试边界并过滤常见凭据");
 var deniedRoot=Path.Combine(root,"blocked-diagnostics");Directory.CreateDirectory(deniedRoot);File.WriteAllText(Path.Combine(deniedRoot,"diagnostics"),"not a directory");var fallbackIncident=new DiagnosticLog(deniedRoot).Record("日志兜底测试",new IOException("QA"));
 Check(fallbackIncident.LogPath.Length>0&&fallbackIncident.StorageWarning.Length>0,"主诊断目录不可写时使用独立的备用日志目录");
 await KumiryoConfigTests.Run(root,Check);
 StartupEntry Entry(bool? enabled=true)=>new(){Id=new string('a',64),Provider="run",Category="普通登录启动",Name="QA fixture",Enabled=enabled,Mode="登录",Command="test.exe --token \"private-value\"",Configuration=new(){{"enabled",enabled?.ToString()??"unknown"}}};
 ScanSnapshot Snapshot(StartupEntry? e,bool complete=true)=>new(){OwnerSid=Contract.Sid,Finished=DateTimeOffset.UtcNow,Entries=e is null?[]:[e],Coverage=[new(){Provider="run",Status=complete?"成功":"失败"}]};
 var entry=Entry();store.SaveSnapshot(Snapshot(entry));store.SaveWatch(ToolboxStore.Watch(entry));store.EvaluateWatches(Snapshot(entry));Check(store.Alerts.Count==0,"符合期望不告警");
 var changed=Entry(false);store.SaveSnapshot(Snapshot(changed));Check(store.Alerts.Count==1&&store.Alerts[0].Delivery=="待发送","偏离先形成持久待通知记录");
 store.EvaluateWatches(Snapshot(changed));Check(store.Alerts.Count==1&&store.Alerts[0].Count==2,"持续偏离合并计数");
 store.EvaluateWatches(Snapshot(null,false));Check(store.Alerts.Count==1&& !store.Alerts[0].Resolved,"采集失败不推断删除或恢复");
 store.EvaluateWatches(Snapshot(Entry(null)));Check(store.Alerts.Count==1,"实际控制状态未知不误报偏离");
 store.EvaluateWatches(Snapshot(entry));Check(store.Alerts[0].Resolved,"恢复正常结案");
 store.EvaluateWatches(Snapshot(changed));Check(store.Alerts.Count==2,"再次偏离形成独立事件");
 var alert=store.Alerts[0];alert.IsRead=true;store.SaveAlert(alert);store.Maintain();Check(store.Alerts.Count==2,"已读和普通日志清理不删除告警");
 var once=ToolboxStore.Watch(entry);once.OneShot=true;store.SaveWatch(once);int old=store.Alerts.Count;store.EvaluateWatches(Snapshot(null));Check(store.Alerts.Count==old,"一次性入口消失不直接判为覆盖");
 store.SaveSnapshot(Snapshot(entry));store.Enqueue(entry.Id);var batch=store.CreateBatch();Check(batch.Items.Count==1,"仅选定条目进入分析批次");Check(!Contract.Encode(batch).Contains("private-value"),"引号包裹的令牌被脱敏");
 var result=new AnalysisResult{BatchId=batch.Id,DeviceId=Contract.DeviceId,Decisions=[new(entry.Id,entry.Fingerprint,"按需保留","测试用途","关闭会影响测试","中")]};
 store.ApplyAnalysis(result);Check(store.Decisions.Count==1&&store.Pending.Count==0,"匹配结果写入建议并移出待分析");
 result.DeviceId="other-machine";Rejected(()=>store.ApplyAnalysis(result),"拒绝跨机器分析结果");result.DeviceId=Contract.DeviceId;
 var stale=Entry(false);store.SaveSnapshot(Snapshot(stale));Rejected(()=>store.ApplyAnalysis(result),"拒绝配置已变化的过期建议");
 Rejected(()=>Contract.Decode<AnalysisResult>("{\"protocol\":1,\"execute\":\"malicious command\"}"),"拒绝含未知执行字段的结果");
 var proc=CommandInspector.ResolveExecutable("\"C:\\Program Files\\Fixture\\tool.exe\" --flag");Check(proc==@"C:\Program Files\Fixture\tool.exe","正确解析有空格的引号路径");
 Check(CommandInspector.ResolveExecutable("not-an-executable") == "","未解析输入不猜成文件路径");
 Check(CommandInspector.Inspect(@"\\offline.example.invalid\share\tool.exe",null).Health=="网络路径未访问","不以高权限访问 UNC 目标");
 Check(CommandInspector.ResolveExecutable("%USERPROFILE%\\app.exe",@"C:\Users\Fixture")==@"C:\Users\Fixture\app.exe","按目标用户展开变量");
 var partial=Snapshot(null,false);store.SaveSnapshot(partial);Check(store.Latest!.Entries.Count==1&&store.Latest.Entries[0].ObservationStatus!="本次读取","部分扫描保留未重新确认的历史入口");
 Rejected(()=>store.ApplyAnalysis(result),"拒绝未重新确认条目的建议");
 Check(store.History.Count>0&&store.GetSnapshot(store.History[0].Id) is not null,"历史快照可索引和读取");
 store.SaveSnapshot(Snapshot(entry));store.SaveNote(entry.Id,"用于远控，不希望中断");
 var annotated=store.Latest!.Entries[0];Check(annotated.UserNote.Contains("远控")&&annotated.Fingerprint!=entry.Fingerprint,"个人用途独立保存并参与分析指纹");
 var annotatedBatch=store.CreateBatch();Check(annotatedBatch.Items[0].Evidence.UserNote.Contains("远控"),"分析证据包含个人用途");
 Rejected(()=>store.ApplyAnalysis(result),"用途变化后拒绝原有建议");
 string previousId=store.Latest!.Id;var cancelled=Snapshot(null);cancelled.Cancelled=true;store.SaveSnapshot(cancelled);Check(store.Latest!.Id==previousId,"取消扫描不覆盖已有完整基线");
 using(var malformed=new MemoryStream(new byte[]{1,0,0,0}))Rejected(()=>Wire.Receive(malformed).GetAwaiter().GetResult(),"IPC 拒绝非法帧长度");
 var schemaRoot=Path.Combine(root,"future-schema");Directory.CreateDirectory(schemaRoot);
 var futureStore=new ToolboxStore(schemaRoot);futureStore.Set("schema","99");
 Rejected(()=>new ToolboxStore(schemaRoot),"新版数据库在执行 DDL 前被拒绝");
 var damagedRoot=Path.Combine(root,"damaged-startup");Directory.CreateDirectory(damagedRoot);
 try
 {
  var damagedPath=Path.Combine(damagedRoot,"toolbox.db");var damagedBytes=System.Text.Encoding.UTF8.GetBytes("invalid SQLite startup fixture");File.WriteAllBytes(damagedPath,damagedBytes);
  try{_ = new ToolboxStore(damagedRoot,verifyIntegrity:true);throw new Exception("错误接受损坏数据库");}
  catch(Microsoft.Data.Sqlite.SqliteException ex)when(ex.SqliteErrorCode is 11 or 26){Check(true,"启动完整性检查拒绝损坏数据库");}
  Check(File.ReadAllBytes(damagedPath).SequenceEqual(damagedBytes)&&Directory.GetFiles(damagedRoot).All(x=>x==damagedPath||x==ToolboxStore.MaintenanceFile(damagedRoot)),"启动检查失败保留原文件且不创建 WAL 或替换数据库");
 }
 finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();foreach(var f in Directory.EnumerateFiles(damagedRoot))File.Delete(f);Directory.Delete(damagedRoot);}
 Check(new ToolboxStore(root,verifyIntegrity:true).Settings is not null,"完整数据库通过启动检查并正常加载");
 var unknown=Entry();unknown.Id=new string('b',64);store.Set("latest",Snapshot(unknown));Check(store.ReconcilePending()==1&&store.Pending.Contains(unknown.Id),"已有未分析结果无需重扫也会补齐待分析区");
 store.RemovePending(unknown.Id);Check(store.ReconcilePending()==0&&!store.Pending.Contains(unknown.Id),"手动暂缓不会被自动入列立刻覆盖");var changedUnknown=Entry(false);changedUnknown.Id=unknown.Id;store.Set("latest",Snapshot(changedUnknown));Check(store.ReconcilePending()==1,"暂缓条目配置变化后重新自动入列");
 var filter=new EntryFilter{App="single-app"};Check(filter.Apply([unknown]).Count==0,"应用筛选只作用于所选视图");filter.Clear();Check(filter.Apply([unknown]).Count==1&&!filter.Active,"重置筛选恢复全部入口");
 var pendingFilter=new EntryFilter();filter.App="other";Check(pendingFilter.Apply([unknown],new HashSet<string>{unknown.Id}).Count==1,"待分析筛选不继承应用筛选");
 Check(EntryQueries.Groups([unknown],"类别").Single().Count==1,"按类别分组计数正确");
 var service=new StartupEntry{Provider="services",Category="服务",OwnerSid="machine",Name="ThirdPartyFixture",Mode="手动",State="Stopped"};ControlPolicy.Validate(service,Contract.Sid,"serviceStart","");Check(true,"普通服务允许独立启动操作");
 try{var driver=new StartupEntry{Provider="services",Category="驱动",OwnerSid="machine"};ControlPolicy.Validate(driver,Contract.Sid,"serviceMode","disabled");throw new Exception("错误允许驱动修改");}catch(InvalidOperationException){Check(true,"拒绝驱动直接修改");}
 service.Name="GameViewerService";try{ControlPolicy.Validate(service,Contract.Sid,"serviceStop","");throw new Exception("错误允许停止远控");}catch(InvalidOperationException){Check(true,"拒绝停止当前远控通道服务");}
 service.Name="ThirdPartyFixture";Rejected(()=>ControlPolicy.Validate(service,Contract.Sid,"serviceMode","disabled & arbitrary"),"拒绝未枚举的服务修改值");
 service.Mode="自动";service.Configuration["delayedAutoStart"]="1";Check(service.ControlValue=="自动（延迟）","关注状态区分普通自动与延迟自动");
 service.Configuration["type"]="96";try{ControlPolicy.Validate(service,Contract.Sid,"serviceMode","disabled");throw new Exception("错误允许用户服务模板修改");}catch(InvalidOperationException){Check(true,"拒绝直接修改用户服务模板");}
 var originEntry=Entry();originEntry.Name="Codex background task";originEntry.Executable=@"C:\Tools\Codex\helper.exe";
 Check(CreationOrigins.Get(originEntry,"Codex").Related&&!CreationOrigins.Get(originEntry,"Codex").Confirmed,"关联名称只能形成线索，不能证明创建者");
 Check(EntryQueries.Groups([],"创建来源").Count()==2,"没有匹配项时也显示两个固定来源分组");
 string originFingerprint=originEntry.Fingerprint;store.SaveOrigin(originEntry,"Codex","用户确认","用户明确确认测试来源");var originSnapshot=Snapshot(originEntry);store.AttachOrigins(originSnapshot);
 Check(CreationOrigins.Get(originEntry,"Codex").Confirmed&&originFingerprint==originEntry.Fingerprint,"人工来源标记独立保存且不改变分析指纹");
 var originFilter=new EntryFilter{Creator="Codex",OriginState="用户确认"};Check(originFilter.Apply([originEntry]).Count==1,"来源筛选区分已确认与名称线索");
 originEntry.Command="replacement.exe";Check(!CreationOrigins.Get(originEntry,"Codex").Confirmed,"目标替换后人工来源确认失效");originEntry.Command=Entry().Command;
 store.SaveOrigin(originEntry,"Codex","已排除","用户确认属于软件自身启动而非创建记录");store.AttachOrigins(originSnapshot);Check(!CreationOrigins.Get(originEntry,"Codex").Related,"明确排除覆盖名称关联线索");
 store.SaveOrigin(originEntry,"Codex","清除标记","");store.AttachOrigins(originSnapshot);Check(CreationOrigins.Get(originEntry,"Codex").State=="线索待确认","清除人工标记后重新显示原始线索");
 Check(CommandInspector.ResolveExecutable(CommandInspector.TaskCommand("\"C:\\Program Files\\Fixture\\tool.exe\"","--flag"))==@"C:\Program Files\Fixture\tool.exe","任务原有引号不重复包装，目标文件仍可解析");
 Check(CommandInspector.ResolveExecutable("%ProgramData%\\Fixture\\agent.exe")==Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),@"Fixture\agent.exe"),"正确展开机器 ProgramData，不依赖当前用户目录");
 var firstAudit=Snapshot(Entry());var sameConfig=Entry();sameConfig.State="Running";Check(AuditComparison.Between(firstAudit,Snapshot(sameConfig)).Changed.Count==0,"运行状态变化不冒充启动配置变化");
 Check(AuditComparison.Between(firstAudit,Snapshot(Entry(false))).Changed.Count==1,"管理员复核识别真实启用配置变化");
 Check(CommandInspector.ResolveShellModule(@"%windir%\System32\webcheck.dll",true)==Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),"webcheck.dll"),"32 位 Shell 模块按对应架构核对文件");
 Check(CommandInspector.ResolveExecutable(@"%CommonProgramFiles%\System\wab32.dll")==Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),@"System\wab32.dll"),"解析 CommonProgramFiles 目标以减少未知项误报");
 var historyOnly=Entry();historyOnly.Id=new string('c',64);historyOnly.ObservationStatus="保留上次结果，本次未确认";store.Set("latest",Snapshot(historyOnly));store.Enqueue(historyOnly.Id);
 try{store.CreateBatch();throw new Exception("错误允许历史保留项提交新分析");}catch(InvalidOperationException){Check(true,"历史保留项不会进入无法提交的分析批次");}
 var mixed=Entry();mixed.Configuration=new(StringComparer.Ordinal){{"command",""},{"CreatorSID","test"}};var roundTrip=Contract.Decode<StartupEntry>(Contract.Encode(mixed));
 Check(mixed.Fingerprint==roundTrip.Fingerprint,"混合大小写配置的指纹在序列化往返后稳定");
 Check(Snapshot(mixed).ConfigurationHash==Snapshot(roundTrip).ConfigurationHash,"配置历史哈希不受字典反序列化排序影响");
 Check(mixed.MatchesFingerprint(mixed.LegacyFingerprint),"旧版 MCP 的等价完整数据指纹可兼容");var legacy=mixed.LegacyFingerprint;mixed.Configuration["CreatorSID"]="changed";
 Check(!mixed.MatchesFingerprint(legacy),"旧格式兼容仍拒绝配置实际变化");
 var corruptHive=Path.Combine(root,"corrupt-hive.dat");File.WriteAllBytes(corruptHive,new byte[32]);Rejected(()=>OfflineHive.ReadStartup(corruptHive),"私有 hive 副本创建后拒绝损坏格式并清理");
 Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();foreach(var f in Directory.EnumerateFiles(schemaRoot))File.Delete(f);Directory.Delete(schemaRoot);
 Console.WriteLine($"通过 {passed} 项功能检查；数据目录 {root}");
}
finally
{
 Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
 // Only this verified, uniquely named fixture directory can be removed recursively.
 var cleanupRoot=Path.GetFullPath(root);var temporaryRoot=Path.GetFullPath(Path.GetTempPath());
 if(!cleanupRoot.StartsWith(temporaryRoot,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(cleanupRoot).StartsWith("TunHotspot-tests-",StringComparison.Ordinal))throw new IOException("测试清理路径越界。");
 if(Directory.Exists(cleanupRoot))Directory.Delete(cleanupRoot,true);
}
