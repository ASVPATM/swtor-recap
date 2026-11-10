using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SwtorRecap.Platform.Windows;

public static class NativeTrayCallbacks
{
    // shellapi.h: NOTIFYICON_VERSION_4 places NIN_SELECT / NIN_KEYSELECT in LOWORD(lParam).
    public static bool OpensDashboard(long parameter)=>(parameter&0xffff) is 0x202 or 0x400 or 0x401;
}

[SupportedOSPlatform("windows")]
public sealed class NativeTray : IDisposable
{
    private delegate IntPtr WindowProc(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct WindowClass
    {
        public uint Size,Style;public WindowProc Procedure;public int ClassExtra,WindowExtra;public IntPtr Instance,Icon,Cursor,Background;
        [MarshalAs(UnmanagedType.LPWStr)]public string? Menu;
        [MarshalAs(UnmanagedType.LPWStr)]public string Name;public IntPtr SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)]private struct Point{public int X,Y;}
    [StructLayout(LayoutKind.Sequential)]private struct Message{public IntPtr Window;public uint Id;public IntPtr WParam,LParam;public uint Time;public Point Point;public uint Private;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct NotifyData
    {
        public uint Size;public IntPtr Window;public uint Id,Flags,Callback;public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Tip;
        public uint State,StateMask;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)]public string Title;
        public uint InfoFlags;public Guid Guid;public IntPtr Balloon;
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern ushort RegisterClassEx(ref WindowClass value);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern bool UnregisterClass(string name,IntPtr instance);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr CreateWindowEx(uint exStyle,string className,string title,uint style,int x,int y,int width,int height,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr parameter);
    [DllImport("user32.dll")]private static extern IntPtr DefWindowProc(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll")]private static extern int GetMessage(out Message message,IntPtr window,uint min,uint max);
    [DllImport("user32.dll")]private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")]private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll")]private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")]private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")]private static extern bool PostMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr LoadIcon(IntPtr instance,IntPtr name);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern bool Shell_NotifyIcon(uint message,ref NotifyData data);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern uint ExtractIconEx(string path,int index,out IntPtr large,out IntPtr small,uint count);
    [DllImport("user32.dll")]private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")]private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern bool AppendMenu(IntPtr menu,uint flags,nuint id,string text);
    [DllImport("user32.dll")]private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")]private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]private static extern uint TrackPopupMenu(IntPtr menu,uint flags,int x,int y,int reserved,IntPtr window,IntPtr rectangle);
    private readonly Thread _thread;private readonly WindowProc _procedure;private readonly ManualResetEventSlim _ready=new();
    private readonly Action<string> _action;private readonly Action<bool> _power;
    private IntPtr _window,_ownedIcon;private uint _taskbar;private NotifyData _data;private Exception? _error;private bool _paused,_active;
    private const uint TrayMessage=0x8001,RefreshMessage=0x8002;
    public NativeTray(Action<string> action,Action<bool> power)
    {
        _action=action;_power=power;_procedure=WndProc;_thread=new Thread(Run){IsBackground=true,Name="SWTOR Recap tray"};_thread.SetApartmentState(ApartmentState.STA);_thread.Start();
        if(!_ready.Wait(TimeSpan.FromSeconds(5)))throw new TimeoutException("Tray initialization timed out.");if(_error is not null)throw new InvalidOperationException("Tray initialization failed.",_error);
    }
    private void Run()
    {
        var instance=GetModuleHandle(null);var name="SwtorRecapTray-"+Guid.NewGuid().ToString("N");
        try
        {
            var wc=new WindowClass{Size=(uint)Marshal.SizeOf<WindowClass>(),Procedure=_procedure,Instance=instance,Name=name};
            if(RegisterClassEx(ref wc)==0)throw new Win32Exception(Marshal.GetLastWin32Error());
            // Hidden top-level window receives Explorer and power broadcasts; not HWND_MESSAGE.
            _window=CreateWindowEx(0,name,"SWTOR Recap",0,0,0,0,0,IntPtr.Zero,IntPtr.Zero,instance,IntPtr.Zero);
            if(_window==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
            _taskbar=RegisterWindowMessage("TaskbarCreated");
            if(Environment.ProcessPath is { } executable&&ExtractIconEx(executable,0,out var large,out var small,1)>0)
            {_ownedIcon=small!=IntPtr.Zero?small:large;if(large!=IntPtr.Zero&&large!=_ownedIcon)DestroyIcon(large);}
            _data=new(){Size=(uint)Marshal.SizeOf<NotifyData>(),Window=_window,Id=1,Flags=1|2|4|0x80,Callback=TrayMessage,Icon=_ownedIcon!=IntPtr.Zero?_ownedIcon:LoadIcon(IntPtr.Zero,new IntPtr(32512)),Tip="SWTOR Recap • Ready for SWTOR",Info="",Title="",Version=4};
            AddIcon();_ready.Set();
            while(GetMessage(out var message,IntPtr.Zero,0,0)>0){TranslateMessage(ref message);DispatchMessage(ref message);}
        }
        catch(Exception ex){_error=ex;_ready.Set();}
        finally{if(_window!=IntPtr.Zero){Shell_NotifyIcon(2,ref _data);DestroyWindow(_window);}if(_ownedIcon!=IntPtr.Zero)DestroyIcon(_ownedIcon);UnregisterClass(name,instance);}
    }
    private void AddIcon(){Shell_NotifyIcon(0,ref _data);_data.Version=4;Shell_NotifyIcon(4,ref _data);}
    private IntPtr WndProc(IntPtr window,uint message,IntPtr wParam,IntPtr lParam)
    {
        if(message==_taskbar&&_taskbar!=0){AddIcon();return IntPtr.Zero;}
        if(message==RefreshMessage){Shell_NotifyIcon(1,ref _data);return IntPtr.Zero;}
        if(message==TrayMessage)
        {
            var code=(uint)(lParam.ToInt64()&0xffff);
            if(NativeTrayCallbacks.OpensDashboard(lParam.ToInt64()))_action("Open");
            if(code is 0x205 or 0x7b)
            {
                var menu=CreatePopupMenu();
                try
                {
                    AppendMenu(menu,0,1,"Open dashboard");AppendMenu(menu,_active?0u:1u,2,_paused?"Resume tracking":"Pause tracking");AppendMenu(menu,0,3,"Settings");AppendMenu(menu,0x800,0,"");AppendMenu(menu,0,4,"Quit tracker");
                    GetCursorPos(out var point);SetForegroundWindow(window);var selection=TrackPopupMenu(menu,0x100|0x2,point.X,point.Y,0,window,IntPtr.Zero);
                    if(selection>0)_action(selection switch{1=>"Open",2=>"Pause",3=>"Settings",_=>"Quit"});PostMessage(window,0,IntPtr.Zero,IntPtr.Zero);
                }
                finally{DestroyMenu(menu);}
            }
            return IntPtr.Zero;
        }
        if(message==0x218){if(wParam.ToInt64()==4)_power(true);if(wParam.ToInt64() is 7 or 18)_power(false);return new IntPtr(1);}
        if(message==0x11){_action("Shutdown");return new IntPtr(1);}
        if(message==0x10){PostQuitMessage(0);return IntPtr.Zero;}
        return DefWindowProc(window,message,wParam,lParam);
    }
    public void Update(string status,bool paused,bool active)
    {_paused=paused;_active=active;var tip="SWTOR Recap • "+status;_data.Tip=tip[..Math.Min(tip.Length,127)];PostMessage(_window,RefreshMessage,IntPtr.Zero,IntPtr.Zero);}
    public void Dispose(){if(_window!=IntPtr.Zero)PostMessage(_window,0x10,IntPtr.Zero,IntPtr.Zero);if(Thread.CurrentThread!=_thread)_thread.Join(TimeSpan.FromSeconds(3));_ready.Dispose();}
}
