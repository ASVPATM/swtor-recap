using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SwtorRecap.Platform.Windows;

[SupportedOSPlatform("windows")]
public static class WindowsAppIdentity
{
    public const string AppId = "ASVPATM.SwtorRecap";
    public static void Apply() => Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(AppId));
    public static void ShowError(string message) => MessageBoxW(IntPtr.Zero, message, "SWTOR Recap", 0x10);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
