using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace TunHotspot.Core;

internal sealed class RegistryChangeWatch : IDisposable
{
    private readonly RegistryKey _key;
    private readonly AutoResetEvent _changed=new(false);
    private readonly Action<string?> _notify;
    private readonly bool _subtree;
    private readonly uint _filter;
    private readonly object _gate=new();
    private RegisteredWaitHandle? _wait;
    private bool _closed;
    public RegistryChangeWatch(RegistryKey key,Action<string?> notify,bool subtree=true,uint filter=5)
    {
        _key=key;_notify=notify;_subtree=subtree;_filter=filter;
        try{Arm();}catch{_key.Dispose();_changed.Dispose();throw;}
    }
    private void Arm()
    {
        int result=RegNotifyChangeKeyValue(_key.Handle,_subtree,_filter|0x10000000,_changed.SafeWaitHandle,true);
        if(result!=0)throw new System.ComponentModel.Win32Exception(result);
        _wait=ThreadPool.RegisterWaitForSingleObject(_changed,(_,_)=>
        {
            lock(_gate)
            {
                if(_closed)return;
                _notify(null);
                try{Arm();}catch(Exception ex){_notify("注册表变化订阅中断，将依靠补扫："+ex.Message);}
            }
        },null,Timeout.Infinite,true);
    }
    public void Dispose(){lock(_gate){if(_closed)return;_closed=true;_wait?.Unregister(null);_key.Dispose();_changed.Dispose();}}
    [DllImport("advapi32.dll")]private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key,[MarshalAs(UnmanagedType.Bool)]bool subtree,uint filter,SafeWaitHandle change,[MarshalAs(UnmanagedType.Bool)]bool asynchronous);
}
