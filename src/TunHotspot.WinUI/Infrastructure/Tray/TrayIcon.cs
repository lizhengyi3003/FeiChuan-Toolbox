using System.Runtime.InteropServices;

namespace TunHotspot;

internal sealed class TrayIcon : IDisposable
{
    private const uint Callback = 0x8000 + 71;
    private readonly nint _window;
    private readonly SubclassProc _procedure;
    private readonly uint _taskbar;
    private readonly uint _exitForUpdate;
    private nint _icon;
    private bool _disposed;
    internal event Action<string>? Command;
    internal event Action<int,int>? MenuRequested;
    internal event Action? SessionEnding;
    internal event Action? UpdateExitRequested;
    internal event Action? FontsChanged;
    internal event Action? Resumed;
    internal TrayIcon(nint window)
    {
        _window = window; _procedure = Procedure;
        _taskbar = RegisterWindowMessage("TaskbarCreated");
        _exitForUpdate = RegisterWindowMessage("TunHotspot.Toolbox.ExitForUpdate.v1");
        _icon = LoadImage(0, Path.Combine(AppContext.BaseDirectory,"Assets","TunHotspot.ico"),1,32,32,0x10);
        bool subclassed = false;
        try
        {
            subclassed = SetWindowSubclass(_window, _procedure, 71, 0);
            if (!subclassed) throw new InvalidOperationException("托盘窗口回调注册失败。");
            Add();
        }
        catch
        {
            if (subclassed) RemoveWindowSubclass(_window, _procedure, 71);
            if (_icon != 0) DestroyIcon(_icon);
            throw;
        }
    }
    private NotifyData Data(string tip="飞船的小工具箱 · 启动与后台监测") => new() { Size=(uint)Marshal.SizeOf<NotifyData>(), Window=_window, Id=71, Flags=1|2|4|0x20|0x80, Callback=Callback, Icon=_icon, Tip=tip[..Math.Min(127,tip.Length)], Guid=new Guid("76D7FD9A-1281-47E6-86C0-BCA9368C1217") };
    private void Add(){var data=Data();if(!Shell_NotifyIcon(0,ref data))throw new InvalidOperationException("系统托盘图标创建失败。");data.Version=4;Shell_NotifyIcon(4,ref data);}
    internal void Update(string tip){if(_disposed)return;var data=Data(tip);Shell_NotifyIcon(1,ref data);}
    private nint Procedure(nint window,uint message,nint wParam,nint lParam,nuint id,nuint data)
    {
        try
        {
        if(message==_taskbar){Add();return 0;}
        if(message==_exitForUpdate){UpdateExitRequested?.Invoke();return 0;}
        if(message==0x0016&&wParam!=0) SessionEnding?.Invoke();
        if(message==0x001D)FontsChanged?.Invoke();
        if(message==0x0218&&(long)wParam is 7 or 18)Resumed?.Invoke();
        if(message==Callback)
        {
            uint action=(uint)((long)lParam&0xffff);
            if(action is 0x0202 or 0x0400 or 0x0401)Command?.Invoke("open");
            else if(action is 0x0205 or 0x007B)
            {
                int x=unchecked((short)((long)wParam&0xffff)),y=unchecked((short)(((long)wParam>>16)&0xffff));
                if(x==-1&&y==-1)
                {
                    var identity=new NotifyIdentity{Size=(uint)Marshal.SizeOf<NotifyIdentity>(),Window=_window,Id=71,Guid=new Guid("76D7FD9A-1281-47E6-86C0-BCA9368C1217")};
                    if(Shell_NotifyIconGetRect(ref identity,out var bounds)==0){x=(bounds.Left+bounds.Right)/2;y=bounds.Top;}else{GetCursorPos(out var point);x=point.X;y=point.Y;}
                }
                MenuRequested?.Invoke(x,y);
            }
            return 0;
        }
        }
        catch(Exception ex){AppLog.Write("tray-callback-error",new{message,Details=ex.ToString()});}
        return DefSubclassProc(window,message,wParam,lParam);
    }
    public void Dispose(){if(_disposed)return;_disposed=true;var data=Data();Shell_NotifyIcon(2,ref data);RemoveWindowSubclass(_window,_procedure,71);if(_icon!=0)DestroyIcon(_icon);}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct NotifyData
    {
        public uint Size;public nint Window;public uint Id,Flags,Callback;public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Tip;
        public uint State,StateMask;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]public string Info;public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)]public string Title;public uint InfoFlags;public Guid Guid;public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)]private struct Point{public int X,Y;}
    [StructLayout(LayoutKind.Sequential)]private struct Rect{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]private struct NotifyIdentity{public uint Size;public nint Window;public uint Id;public Guid Guid;}
    private delegate nint SubclassProc(nint hwnd,uint message,nint wParam,nint lParam,nuint id,nuint data);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern bool Shell_NotifyIcon(uint message,ref NotifyData data);
    [DllImport("comctl32.dll")]private static extern bool SetWindowSubclass(nint hwnd,SubclassProc proc,nuint id,nuint data);
    [DllImport("comctl32.dll")]private static extern bool RemoveWindowSubclass(nint hwnd,SubclassProc proc,nuint id);
    [DllImport("comctl32.dll")]private static extern nint DefSubclassProc(nint hwnd,uint message,nint wParam,nint lParam);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern uint RegisterWindowMessage(string value);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern nint LoadImage(nint instance,string name,uint type,int x,int y,uint flags);
    [DllImport("user32.dll")]private static extern bool DestroyIcon(nint icon);
    [DllImport("shell32.dll")]private static extern int Shell_NotifyIconGetRect(ref NotifyIdentity identity,out Rect bounds);
    [DllImport("user32.dll")]private static extern bool GetCursorPos(out Point point);
}
