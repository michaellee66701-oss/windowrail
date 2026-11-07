using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace WindowRail;

public enum EntryKind { Window, Program }

public sealed class EntryConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public EntryKind Kind { get; set; }
    public string ExePath { get; set; } = "";
    public string LastTitle { get; set; } = "";
    public string AppName { get; set; } = "";
    public string Alias { get; set; } = "";
}

public sealed class RailConfig
{
    public int SchemaVersion { get; set; } = 1;
    public double Width { get; set; } = 224;
    public double SurfaceOpacity { get; set; }
    public bool DarkText { get; set; }
    public string Monitor { get; set; } = "";
    public double AnchorX { get; set; } = 0.94;
    public double AnchorY { get; set; } = 0.35;
    public List<EntryConfig> Entries { get; set; } = new();
}

public readonly record struct WindowIdentity(nint Handle, uint ProcessId, long ProcessBirth);

public sealed record WindowInfo(WindowIdentity Identity, string Title, string ExePath,
    string AppName, bool OnCurrentDesktop, bool Minimized)
{
    public nint Handle => Identity.Handle;
    public string Label => WindowRules.CleanTitle(Title);
}

public static class WindowRules
{
    public static bool SamePath(string? a, string? b) => !string.IsNullOrWhiteSpace(a) &&
        !string.IsNullOrWhiteSpace(b) && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    public static string CleanTitle(string title)
    {
        string clean = Regex.Replace(title.Trim(), @"\s+[-–—]\s+(?:Microsoft\s+)?(?:Word|Excel|PowerPoint)(?:\s*\([^)]*\))?$", "", RegexOptions.IgnoreCase);
        if (clean != title.Trim()) clean = Regex.Replace(clean, @"\s+[-–—]\s+(?:兼容性模式|Compatibility Mode)$", "", RegexOptions.IgnoreCase);
        return string.IsNullOrWhiteSpace(clean) ? title : clean;
    }

    // Restore only a unique exact caption/path match. Ambiguous documents must never be guessed.
    public static WindowInfo? Restore(EntryConfig entry, IEnumerable<WindowInfo> windows)
    {
        var candidates = windows.Where(w => SamePath(w.ExePath, entry.ExePath) &&
            string.Equals(w.Title, entry.LastTitle, StringComparison.Ordinal)).Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    public static WindowInfo? SelectProgram(EntryConfig entry, IEnumerable<WindowInfo> windows,
        IReadOnlyDictionary<WindowIdentity, long> recent)
        => windows.Where(w => SamePath(w.ExePath, entry.ExePath) && w.OnCurrentDesktop)
            .OrderByDescending(w => recent.TryGetValue(w.Identity, out long order) ? order : 0).FirstOrDefault();

    public static bool CanPin(WindowInfo window) => !string.IsNullOrWhiteSpace(window.ExePath) &&
        Path.IsPathFullyQualified(window.ExePath) && window.ExePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    public static bool WordDocumentReturnedHome(EntryConfig entry, WindowInfo next) =>
        string.Equals(Path.GetFileName(entry.ExePath), "WINWORD.EXE", StringComparison.OrdinalIgnoreCase) &&
        Regex.IsMatch(entry.LastTitle, @"\s+[-–—]\s+(?:Microsoft\s+)?Word$", RegexOptions.IgnoreCase) &&
        Regex.IsMatch(next.Title.Trim(), @"^(?:Microsoft\s+)?Word$", RegexOptions.IgnoreCase);
}

public sealed class RailEntry : INotifyPropertyChanged
{
    public EntryConfig Config { get; }
    public WindowInfo? Window { get; private set; }
    public ImageSource? Icon { get; set; }
    public bool Active { get; private set; }
    public bool Available { get; private set; }
    public bool Launching { get; set; }
    public bool IsProgram => Config.Kind == EntryKind.Program;
    public string Label => !string.IsNullOrWhiteSpace(Config.Alias) ? Config.Alias :
        IsProgram ? Config.AppName : WindowRules.CleanTitle(Window?.Title ?? Config.LastTitle);
    public string AccessibleName => (IsProgram ? "打开程序 " : "切换窗口 ") + Label;
    public string Hint => IsProgram ? $"{Label}\n固定程序 · {(Launching ? "正在启动" : Available ? "点击切换，右键选择窗口" : "点击启动")}\n{Config.ExePath}" :
        $"{Window?.Title ?? Config.LastTitle}\n{Config.AppName} · {(Window is { OnCurrentDesktop: false } ? "位于其他虚拟桌面" : "点击切换 · 拖动排序 · 右键更多")}";
    public double ContentOpacity => IsProgram && !Available ? 0.58 : 1;
    public string Fallback => Config.AppName.Contains("Word", StringComparison.OrdinalIgnoreCase) ? "W" :
        Config.AppName.Length > 0 ? Config.AppName[..1].ToUpperInvariant() : "▧";

    public RailEntry(EntryConfig config, WindowInfo? window = null) { Config = config; Window = window; }
    public void Update(WindowInfo? window, bool active)
    {
        if (Window == window && Active == active && Available == (window is { OnCurrentDesktop: true })) return;
        Window = window; Active = active; Available = window is { OnCurrentDesktop: true };
        if (!IsProgram && window != null) Config.LastTitle = window.Title;
        Refresh();
    }
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class WindowChoice : INotifyPropertyChanged
{
    private bool selected;
    public WindowInfo Info { get; }
    public bool IsSelected { get => selected; set { selected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); } }
    public string Label => Info.Label;
    public string Detail => Info.AppName + (Info.Minimized ? " · 已最小化" : "") + (AlreadyAdded ? " · 已添加" : "");
    public string Hint => Info.Title + "\n" + Info.ExePath;
    public bool AlreadyAdded { get; }
    public bool CanSelect => !AlreadyAdded;
    public ImageSource? Icon { get; }
    public WindowChoice(WindowInfo info, bool alreadyAdded, ImageSource? icon) { Info = info; AlreadyAdded = alreadyAdded; Icon = icon; }
    public event PropertyChangedEventHandler? PropertyChanged;
}
