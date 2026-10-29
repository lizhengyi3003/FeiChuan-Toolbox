using System.Runtime.InteropServices;

namespace TunHotspot;

internal sealed class TrayIcon : IDisposable
{
    private const uint Callback = 0x8000 + 71;
    private readonly nint _window;
    private readonly SubclassProc _procedure;
    private readonly uint _taskbar;
    private nint _icon;
    private bool _disposed;
    private readonly Func<bool> _paused;
    internal event Action<string>? Command;
    internal event Action? SessionEnding;
    internal event Action? FontsChanged;
    internal event Action? Resumed;
    internal TrayIcon(nint window, Func<bool> paused)
    {
        _window = window; _paused = paused; _procedure = Procedure;
        _taskbar = RegisterWindowMessage("TaskbarCreated");
        _icon = LoadImage(0, Path.Combine(AppContext.BaseDirectory,"Assets","TunHotspot.ico"),1,32,32,0x10);
        if (!SetWindowSubclass(_window,_procedure,71,0)) throw new InvalidOperationException("托盘窗口回调注册失败。");
        Add();
    }
    private NotifyData Data(string tip="TunHotspot · 启动与后台监测") => new() { Size=(uint)Marshal.SizeOf<NotifyData>(), Window=_window, Id=71, Flags=1|2|4|0x20, Callback=Callback, Icon=_icon, Tip=tip[..Math.Min(127,tip.Length)], Guid=new Guid("76D7FD9A-1281-47E6-86C0-BCA9368C1217") };
    private void Add(){var data=Data();if(!Shell_NotifyIcon(0,ref data))throw new InvalidOperationException("系统托盘图标创建失败。");data.Version=4;Shell_NotifyIcon(4,ref data);}
    internal void Update(string tip){if(_disposed)return;var data=Data(tip);Shell_NotifyIcon(1,ref data);}
    private nint Procedure(nint window,uint message,nint wParam,nint lParam,nuint id,nuint data)
    {
        if(message==_taskbar){try{Add();}catch{}return 0;}
        if(message is 0x0011 or 0x0016) SessionEnding?.Invoke();
        if(message==0x001D)FontsChanged?.Invoke();
        if(message==0x0218&&(long)wParam is 7 or 18)Resumed?.Invoke();
        if(message==Callback)
        {
            uint action=(uint)((long)lParam&0xffff);
            if(action is 0x0202 or 0x0400 or 0x0401)Command?.Invoke("open");
            else if(action is 0x0205 or 0x007B)Menu();
            return 0;
        }
        return DefSubclassProc(window,message,wParam,lParam);
    }
    private void Menu()
    {
        var menu=CreatePopupMenu();
        try
        {
            AppendMenu(menu,0,1,"打开工具箱");AppendMenu(menu,0,2,"全局扫描（管理员）");AppendMenu(menu,0,3,"通知与事件");
            AppendMenu(menu,0,4,_paused()?"恢复监测":"暂停监测");AppendMenu(menu,0x800,0,"");AppendMenu(menu,0,5,"退出托盘（机器采集可能继续）");
            GetCursorPos(out var point);SetForegroundWindow(_window);uint choice=TrackPopupMenu(menu,0x100|2,point.X,point.Y,0,_window,0);PostMessage(_window,0,0,0);
            if(choice>0)Command?.Invoke(choice switch{1=>"open",2=>"scan",3=>"alerts",4=>"pause",_=>"exit"});
        }
        finally{DestroyMenu(menu);}
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
    private delegate nint SubclassProc(nint hwnd,uint message,nint wParam,nint lParam,nuint id,nuint data);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern bool Shell_NotifyIcon(uint message,ref NotifyData data);
    [DllImport("comctl32.dll")]private static extern bool SetWindowSubclass(nint hwnd,SubclassProc proc,nuint id,nuint data);
    [DllImport("comctl32.dll")]private static extern bool RemoveWindowSubclass(nint hwnd,SubclassProc proc,nuint id);
    [DllImport("comctl32.dll")]private static extern nint DefSubclassProc(nint hwnd,uint message,nint wParam,nint lParam);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern uint RegisterWindowMessage(string value);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern nint LoadImage(nint instance,string name,uint type,int x,int y,uint flags);
    [DllImport("user32.dll")]private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")]private static extern nint CreatePopupMenu();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern bool AppendMenu(nint menu,uint flags,nuint id,string text);
    [DllImport("user32.dll")]private static extern uint TrackPopupMenu(nint menu,uint flags,int x,int y,int reserved,nint hwnd,nint rectangle);
    [DllImport("user32.dll")]private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")]private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")]private static extern bool PostMessage(nint hwnd,uint message,nint wParam,nint lParam);
}
