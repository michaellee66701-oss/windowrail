using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WindowRail;

public sealed class ConfigStore
{
    public string DirectoryPath { get; }
    private readonly string path;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public string? RecoveryMessage { get; private set; }
    public ConfigStore(string directory) { DirectoryPath = Path.GetFullPath(directory); Directory.CreateDirectory(DirectoryPath); path = Path.Combine(DirectoryPath, "settings.json"); }
    public RailConfig Load()
    {
        if (!File.Exists(path)) return new();
        foreach (string candidate in new[] { path, path + ".bak" })
        {
            try
            {
                var config = JsonSerializer.Deserialize<RailConfig>(File.ReadAllText(candidate), Options) ?? throw new InvalidDataException();
                if (config.SchemaVersion != 1) throw new InvalidDataException("未知配置版本");
                config.Width = double.IsFinite(config.Width) ? Math.Clamp(config.Width, 180, 360) : 224;
                config.AnchorX = double.IsFinite(config.AnchorX) ? Math.Clamp(config.AnchorX, 0, 1) : 0.94;
                config.AnchorY = double.IsFinite(config.AnchorY) ? Math.Clamp(config.AnchorY, 0, 1) : 0.35;
                config.SurfaceOpacity = double.IsFinite(config.SurfaceOpacity) ? Math.Clamp(config.SurfaceOpacity, 0, 0.8) : 0;
                config.Entries ??= new();
                config.Entries = config.Entries.Where(e => e != null && Enum.IsDefined(e.Kind)).ToList();
                foreach (var e in config.Entries) { e.ExePath ??= ""; e.LastTitle ??= ""; e.AppName ??= ""; e.Alias ??= ""; }
                if (candidate != path) { File.Copy(candidate, path, true); RecoveryMessage = "已从备份恢复配置"; }
                return config;
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException) { Log("load", ex); }
        }
        File.Copy(path, path + ".invalid-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
        RecoveryMessage = "配置无法读取，已保留原文件并恢复默认设置";
        return new();
    }
    public void Save(RailConfig config)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(config, Options));
        if (File.Exists(path)) File.Copy(path, path + ".bak", true);
        File.Move(temporary, path, true);
    }
    public void Log(string operation, Exception? error = null)
    {
        try { string log = Path.Combine(DirectoryPath, "diagnostics.log"); if (File.Exists(log) && new FileInfo(log).Length > 512_000) File.Move(log, log + ".previous", true); File.AppendAllText(log, $"{DateTimeOffset.Now:O} {operation} {error?.GetType().Name} 0x{error?.HResult:X8}\n"); } catch { }
    }
}

public sealed class WindowCatalog
{
    private readonly uint ownPid = (uint)Environment.ProcessId;
    private readonly ConcurrentDictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);
    public Task<List<WindowInfo>> ScanAsync() => Task.Run(Scan);
    public List<WindowInfo> Scan()
    {
        var windows = new List<WindowInfo>();
        var processes = new Dictionary<uint, (string Path, long Birth)>();
        IVirtualDesktopManager? desktops = null;
        try { desktops = (IVirtualDesktopManager?)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A"))!); } catch { }
        try
        {
            Native.EnumWindows((hwnd, _) =>
            {
                if (!Native.IsWindowVisible(hwnd)) return true;
                Native.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == ownPid || pid == 0) return true;
                long style = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
                if ((style & Native.WS_EX_TOOLWINDOW) != 0) return true;
                if (Native.GetWindow(hwnd, 4) != 0 && (style & Native.WS_EX_APPWINDOW) == 0) return true;
                string title = Native.Caption(hwnd);
                if (string.IsNullOrWhiteSpace(title) || title == "Program Manager") return true;
                bool current = true;
                try { if (desktops != null && desktops.IsWindowOnCurrentVirtualDesktop(hwnd, out bool result) == 0) current = result; } catch { }
                if (Native.DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0 && current) return true;
                if (!processes.TryGetValue(pid, out var process)) { process = Native.ProcessInfo(pid); processes[pid] = process; }
                string name = string.IsNullOrEmpty(process.Path) ? "应用窗口" : names.GetOrAdd(process.Path, FriendlyName);
                windows.Add(new(new(hwnd, pid, process.Birth), title, process.Path, name, current, Native.IsIconic(hwnd)));
                return true;
            }, 0);
        }
        finally { if (desktops != null) Marshal.ReleaseComObject(desktops); }
        return windows;
    }
    public static string FriendlyName(string path)
    {
        string file = Path.GetFileNameWithoutExtension(path);
        if (file.Equals("WINWORD", StringComparison.OrdinalIgnoreCase)) return "Word";
        if (file.Equals("EXCEL", StringComparison.OrdinalIgnoreCase)) return "Excel";
        if (file.Equals("POWERPNT", StringComparison.OrdinalIgnoreCase)) return "PowerPoint";
        if (file.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return "文件资源管理器";
        try { return FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } description ? description : file; } catch { return file; }
    }
}

public sealed class IconCache
{
    private readonly Dictionary<string, ImageSource?> images = new(StringComparer.OrdinalIgnoreCase);
    public ImageSource? Get(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (images.TryGetValue(path, out var image)) return image;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon != null) { var bitmap = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(24, 24)); bitmap.Freeze(); image = bitmap; }
        }
        catch { image = null; }
        images[path] = image;
        return image;
    }
}

public sealed class WindowEvents : IDisposable
{
    private readonly Native.WinEventProc callback;
    private readonly List<nint> hooks = new();
    public event Action? Changed;
    public event Action<nint>? ForegroundChanged;
    public WindowEvents()
    {
        callback = OnEvent;
        hooks.Add(Native.SetWinEventHook(3, 3, 0, callback, 0, 0, 2));
        hooks.Add(Native.SetWinEventHook(0x8000, 0x800C, 0, callback, 0, 0, 2));
        hooks.Add(Native.SetWinEventHook(0x0016, 0x0017, 0, callback, 0, 0, 2));
    }
    private void OnEvent(nint hook, uint id, nint hwnd, int objectId, int childId, uint thread, uint time)
    {
        if (hwnd == 0) return;
        if (id == 3) { ForegroundChanged?.Invoke(hwnd); Changed?.Invoke(); }
        else if (id is 0x0016 or 0x0017 || (objectId == 0 && childId == 0 && id is 0x8000 or 0x8001 or 0x8002 or 0x8003 or 0x800C)) Changed?.Invoke();
    }
    public void Dispose() { foreach (var hook in hooks) if (hook != 0) Native.UnhookWinEvent(hook); GC.KeepAlive(callback); }
}

public sealed class SwitchService
{
    private CancellationTokenSource? pending;
    public void Cancel() { pending?.Cancel(); }
    public async Task<string?> SwitchAsync(WindowInfo window)
    {
        Cancel();
        using var tokenSource = new CancellationTokenSource(); pending = tokenSource;
        try
        {
            if (!Native.SameWindow(window.Identity)) return "这个窗口已关闭，请重新添加";
            if (!window.OnCurrentDesktop) return "此窗口位于其他虚拟桌面，请先切换桌面";
            uint input = Native.InputStamp();
            if (Native.IsIconic(window.Handle))
            {
                Native.ShowWindowAsync(window.Handle, 9);
                for (int i = 0; i < 6 && Native.IsIconic(window.Handle); i++) await Task.Delay(45, tokenSource.Token);
                if (input != Native.InputStamp()) return null;
            }
            if (!Native.SameWindow(window.Identity)) return "这个窗口已关闭";
            nint target = window.Handle;
            nint popup = Native.GetLastActivePopup(window.Handle);
            if (popup != 0 && Native.IsWindowVisible(popup) && Native.IsWindowEnabled(popup)) target = popup;
            Native.SetForegroundWindow(target);
            await Task.Delay(85, tokenSource.Token);
            if (input != Native.InputStamp()) return null;
            if (Native.IsForeground(target)) return null;
            Native.Flash(window.Handle);
            return "系统暂未允许切换，可以再点击一次";
        }
        catch (OperationCanceledException) { return null; }
        finally { if (ReferenceEquals(pending, tokenSource)) pending = null; }
    }
}
