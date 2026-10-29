using TunHotspot.Core;

int passed=0;
void Check(bool condition,string message){if(!condition)throw new Exception(message);passed++;Console.WriteLine("PASS "+message);}
void Rejected(Action action,string message){try{action();}catch(Exception ex)when(ex is InvalidDataException or System.Text.Json.JsonException){Check(true,message);return;}throw new Exception("未拒绝："+message);}
var root=Path.Combine(Path.GetTempPath(),"TunHotspot-tests-"+Guid.NewGuid().ToString("N"));
try
{
 var store=new ToolboxStore(root);
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
 Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();foreach(var f in Directory.EnumerateFiles(schemaRoot))File.Delete(f);Directory.Delete(schemaRoot);
 Console.WriteLine($"通过 {passed} 项功能检查；数据目录 {root}");
}
finally
{
 Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
 // Only files created directly in this unique fixture directory are cleaned, without recursive path operations.
 if(Directory.Exists(root)){foreach(var f in Directory.EnumerateFiles(root))File.Delete(f);Directory.Delete(root);}
}
