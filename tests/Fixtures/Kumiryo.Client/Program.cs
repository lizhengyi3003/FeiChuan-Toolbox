using System.Runtime.InteropServices;

// A disposable native message-loop fixture. It does not run a proxy, touch Windows
// network settings, interpret a Kumiryo profile, or contain real credentials.
internal static class Program
{
    private static readonly WndProc Callback = Procedure;
    private static int Main()
    {
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "fixture-starts.log"), Environment.ProcessId + Environment.NewLine);
        nint instance = GetModuleHandle(null);
        var klass = new WindowClass { Procedure = Callback, Instance = instance, ClassName = "FLUTTER_RUNNER_WIN32_WINDOW", Background = 6 };
        if (RegisterClass(ref klass) == 0) return 2;
        nint window = CreateWindowEx(0, klass.ClassName, "TunHotspot Kumiryo QA Fixture", 0x00CF0000, 60, 60, 500, 180, 0, 0, instance, 0);
        if (window == 0) return 3;
        ShowWindow(window, 5);
        while (true)
        {
            int result = GetMessage(out var message, 0, 0, 0);
            if (result < 0) return 4;
            // A framework thread can consume WM_QUIT without ending the application.
            // The old single-loop mock incorrectly treated it as proof of a real exit.
            if (result == 0) { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "fixture-unsafe-quit.log"), "WM_QUIT\n"); continue; }
            TranslateMessage(ref message); DispatchMessage(ref message);
        }
    }
    private static nint Procedure(nint window, uint message, nint w, nint l)
    {
        if (message == 0x0010) { ShowWindow(window, 0); return 0; } // Mimic minimize-on-close.
        if (message == 0x0002) { PostQuitMessage(0); return 0; }
        return DefWindowProc(window, message, w, l);
    }
    private delegate nint WndProc(nint window, uint message, nint w, nint l);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Style; public WndProc Procedure; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background;
        public string? MenuName; public string ClassName;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nint W, L; public uint Time; public int X, Y; public uint Private; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClass(ref WindowClass data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint extra, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint data);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, nint window, uint minimum, uint maximum);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint window, uint message, nint w, nint l);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
}
