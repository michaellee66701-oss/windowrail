using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowRail;

internal static class Native
{
    internal const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x08000000;
    internal const int WM_MOUSEACTIVATE = 0x21, WM_DPICHANGED = 0x02E0, MA_NOACTIVATE = 3;
    internal const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10, SWP_NOZORDER = 4;
    internal delegate bool EnumProc(nint hwnd, nint parameter);
    internal delegate void WinEventProc(nint hook, uint eventId, nint hwnd, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc proc, nint parameter);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] internal static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] internal static extern nint GetLastActivePopup(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] internal static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool ShowWindowAsync(nint hwnd, int command);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FLASHWINFO info);
    [DllImport("user32.dll")] internal static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc callback, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(nint hook);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);
    [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint Size, Time; }
    [StructLayout(LayoutKind.Sequential)] private struct FLASHWINFO { public uint Size; public nint Hwnd; public uint Flags, Count, Timeout; }
    internal static string Caption(nint hwnd) { var b = new StringBuilder(2048); GetWindowTextW(hwnd, b, b.Capacity); return b.ToString(); }
    internal static (string Path, long Birth) ProcessInfo(uint pid)
    {
        nint handle = OpenProcess(0x1000, false, pid);
        if (handle == 0) return ("", 0);
        try { var path = new StringBuilder(32768); int size = path.Capacity; QueryFullProcessImageName(handle, 0, path, ref size); GetProcessTimes(handle, out long birth, out _, out _, out _); return (path.ToString(), birth); }
        finally { CloseHandle(handle); }
    }
    internal static bool SameWindow(WindowIdentity identity)
    {
        if (!IsWindow(identity.Handle)) return false;
        GetWindowThreadProcessId(identity.Handle, out uint pid);
        if (pid != identity.ProcessId) return false;
        return identity.ProcessBirth == 0 || ProcessInfo(pid).Birth == identity.ProcessBirth;
    }
    internal static uint InputStamp() { var info = new LASTINPUTINFO { Size = (uint)Marshal.SizeOf<LASTINPUTINFO>() }; GetLastInputInfo(ref info); return info.Time; }
    internal static void Flash(nint hwnd) { var info = new FLASHWINFO { Size = (uint)Marshal.SizeOf<FLASHWINFO>(), Hwnd = hwnd, Flags = 2, Count = 2 }; FlashWindowEx(ref info); }
    internal static bool IsForeground(nint hwnd)
    {
        nint front = GetForegroundWindow();
        return front == hwnd || (front != 0 && GetAncestor(front, 3) == GetAncestor(hwnd, 3));
    }
}

[ComImport, Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVirtualDesktopManager
{
    [PreserveSig] int IsWindowOnCurrentVirtualDesktop(nint hwnd, [MarshalAs(UnmanagedType.Bool)] out bool current);
    [PreserveSig] int GetWindowDesktopId(nint hwnd, out Guid id);
    [PreserveSig] int MoveWindowToDesktop(nint hwnd, ref Guid id);
}
