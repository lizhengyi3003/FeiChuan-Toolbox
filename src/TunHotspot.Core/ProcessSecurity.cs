using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace TunHotspot.Core;

public static class ProcessSecurity
{
    public sealed record NativeService(string Name,string DisplayName,string State,int Pid,uint Type);
    public static List<NativeService> Services()
    {
        var result=new List<NativeService>();var manager=OpenSCManager(null,null,4);
        if(manager==0)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            foreach(uint type in new uint[]{0x30,0xB})
            {
                uint resume=0;int size=262144;var buffer=Marshal.AllocHGlobal(size);
                try
                {
                    bool more;
                    do
                    {
                        bool ok=EnumServicesStatusEx(manager,0,type,3,buffer,size,out int needed,out int count,ref resume,null);
                        int error=Marshal.GetLastWin32Error();if(!ok&&error!=234)throw new System.ComponentModel.Win32Exception(error);
                        int stride=Marshal.SizeOf<EnumeratedService>();
                        for(int i=0;i<count;i++)
                        {
                            var s=Marshal.PtrToStructure<EnumeratedService>(buffer+i*stride);
                            result.Add(new(Marshal.PtrToStringUni(s.Name)??"",Marshal.PtrToStringUni(s.Display)??"",s.Status.State switch{1=>"Stopped",2=>"StartPending",3=>"StopPending",4=>"Running",7=>"Paused",_=>"Unknown"},checked((int)s.Status.ProcessId),s.Status.Type));
                        }
                        more=!ok&&error==234;if(more&&count==0)throw new IOException("SCM 枚举缓冲区不足。");
                    }while(more);
                }
                finally{Marshal.FreeHGlobal(buffer);}
            }
        }
        finally{CloseServiceHandle(manager);}
        return result.DistinctBy(x=>x.Name,StringComparer.OrdinalIgnoreCase).ToList();
    }
    public static List<ProcessRecord> Processes()
    {
        var result=new List<ProcessRecord>();var snapshot=CreateToolhelp32Snapshot(2,0);
        if(snapshot==new nint(-1))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var entry=new ProcessEntry{Size=(uint)Marshal.SizeOf<ProcessEntry>()};
            if(!Process32First(snapshot,ref entry))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            do
            {
                var p=new ProcessRecord{Pid=checked((int)entry.Pid),ParentPid=checked((int)entry.Parent),Name=entry.Name,Source="ToolHelp 当前进程快照"};
                var handle=OpenProcess(0x1000,false,p.Pid);
                if(handle!=0)
                {
                    try{var buffer=new System.Text.StringBuilder(32768);uint length=32768;if(QueryFullProcessImageName(handle,0,buffer,ref length))p.Path=buffer.ToString();
                        if(GetProcessTimes(handle,out long created,out _,out _,out _))p.Created=DateTimeOffset.FromFileTime(created);}
                    finally{CloseHandle(handle);}
                }
                result.Add(p);entry.Size=(uint)Marshal.SizeOf<ProcessEntry>();
            }while(Process32Next(snapshot,ref entry));
        }
        finally{CloseHandle(snapshot);}
        return result;
    }
    public static string UserSid(int pid)
    {
        var process=OpenProcess(0x1000,false,pid); if(process==0)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!OpenProcessToken(process, 8, out var token)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try { using var identity = new WindowsIdentity(token); return identity.User?.Value ?? ""; }
            finally { CloseHandle(token); }
        }
        finally{CloseHandle(process);}
    }
    public static uint PipeClient(System.IO.Pipes.NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint id)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); return id;
    }
    public static uint PipeServer(System.IO.Pipes.NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint id)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); return id;
    }
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern nint OpenProcess(uint access,bool inherit,int pid);
    public static uint ServicePid(string name)
    {
        var manager=OpenSCManager(null,null,1);if(manager==0)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var service=OpenService(manager,name,4);if(service==0)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try{if(!QueryServiceStatusEx(service,0,out var status,Marshal.SizeOf<ServiceStatus>(),out _))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());return status.ProcessId;}
            finally{CloseServiceHandle(service);}
        }
        finally{CloseServiceHandle(manager);}
    }
    [StructLayout(LayoutKind.Sequential)]private struct ServiceStatus{public uint Type,State,Accepted,Exit,Specific,CheckPoint,Wait,ProcessId,Flags;}
    [StructLayout(LayoutKind.Sequential)]private struct EnumeratedService{public nint Name,Display;public ServiceStatus Status;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct ProcessEntry{public uint Size,Usage,Pid;public nuint Heap;public uint Module,Threads,Parent;public int Priority;public uint Flags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string Name;}
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool EnumServicesStatusEx(nint manager,int level,uint type,uint state,nint buffer,int bytes,out int needed,out int count,ref uint resume,string? group);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern nint OpenSCManager(string? machine,string? database,uint access);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern nint OpenService(nint manager,string name,uint access);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool QueryServiceStatusEx(nint service,int level,out ServiceStatus status,int size,out int needed);
    [DllImport("advapi32.dll")]private static extern bool CloseServiceHandle(nint handle);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern nint CreateToolhelp32Snapshot(uint flags,uint pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,EntryPoint="Process32FirstW",SetLastError=true)]private static extern bool Process32First(nint snapshot,ref ProcessEntry entry);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,EntryPoint="Process32NextW",SetLastError=true)]private static extern bool Process32Next(nint snapshot,ref ProcessEntry entry);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool QueryFullProcessImageName(nint process,uint flags,System.Text.StringBuilder buffer,ref uint size);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetProcessTimes(nint process,out long creation,out long exit,out long kernel,out long user);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(nint pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(nint pipe, out uint pid);
}
