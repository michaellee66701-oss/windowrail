using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace WindowRail;

public partial class MainWindow : Window
{
    public ObservableCollection<RailEntry> Entries { get; } = new();
    private readonly ConfigStore store;
    private readonly RailConfig config;
    private readonly WindowCatalog catalog = new();
    private readonly IconCache icons = new();
    private readonly SwitchService switcher = new();
    private readonly Dictionary<WindowIdentity, long> recent = new();
    private readonly DispatcherTimer refreshTimer, debounceTimer, saveTimer, toastTimer;
    private List<WindowInfo> windows = new();
    private WindowEvents? events;
    private Forms.NotifyIcon? tray;
    private WindowPicker? picker;
    private nint hwnd;
    private bool ready, scanning, closing, positioning;
    private Point dragStart;
    private RailEntry? dragEntry;
    private CancellationTokenSource? launchCancellation;
    private long activationOrder;
    private long requestVersion;
    private readonly bool inspectable;

    public MainWindow(ConfigStore store, bool inspectable = false)
    {
        this.store = store; this.inspectable = inspectable; config = store.Load();
        InitializeComponent(); DataContext = this; Width = config.Width;
        if (inspectable) { ShowInTaskbar = true; Title = "窗栏 · 验证窗口"; }
        refreshTimer = Timer(TimeSpan.FromSeconds(5), async () => await RefreshAsync());
        debounceTimer = Timer(TimeSpan.FromMilliseconds(220), async () => { debounceTimer!.Stop(); await RefreshAsync(); });
        saveTimer = Timer(TimeSpan.FromMilliseconds(600), () => { saveTimer!.Stop(); Persist(); });
        toastTimer = Timer(TimeSpan.FromSeconds(5), () => { toastTimer!.Stop(); ToastBorder.Visibility = Visibility.Collapsed; });
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        LocationChanged += (_, _) => { if (ready && !positioning) ScheduleSave(); };
        SizeChanged += (_, _) => { if (ready) { ClampPosition(); ScheduleSave(); } };
        Closed += OnClosed;
        SystemEvents.DisplaySettingsChanged += OnDisplayChange;
        ApplyAppearance();
    }
    private DispatcherTimer Timer(TimeSpan interval, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = interval };
        timer.Tick += (_, _) => action(); return timer;
    }
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        hwnd = new WindowInteropHelper(this).Handle;
        nint style = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE) | Native.WS_EX_NOACTIVATE;
        style = inspectable ? (style & ~Native.WS_EX_TOOLWINDOW) | Native.WS_EX_APPWINDOW : style | Native.WS_EX_TOOLWINDOW;
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, style);
        HwndSource.FromHwnd(hwnd)?.AddHook(WindowHook);
    }
    private nint WindowHook(nint handle, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == Native.WM_MOUSEACTIVATE) { handled = true; return Native.MA_NOACTIVATE; }
        if (message == Native.WM_DPICHANGED) Dispatcher.BeginInvoke(() => { UpdateMaxHeight(); ClampPosition(); });
        return 0;
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        CreateTray();
        windows = await catalog.ScanAsync();
        int skipped = 0;
        foreach (var saved in config.Entries.ToArray())
        {
            if (saved.Kind == EntryKind.Program) Entries.Add(CreateEntry(saved));
            else
            {
                var match = WindowRules.Restore(saved, windows);
                if (match != null && !Entries.Any(e => e.Window?.Identity == match.Identity)) Entries.Add(CreateEntry(saved, match));
                else skipped++;
            }
        }
        ready = true;
        events = new WindowEvents();
        events.Changed += QueueRefresh;
        events.ForegroundChanged += OnForeground;
        refreshTimer.Start();
        UpdateEntries(); UpdateMaxHeight(); UpdateLayout(); RestorePosition();
        if (store.RecoveryMessage != null) Toast(store.RecoveryMessage);
        else if (skipped > 0) Toast($"{skipped} 个窗口已关闭或无法唯一识别，可通过 ＋ 重新添加");
        ScheduleSave();
    }
    private RailEntry CreateEntry(EntryConfig entry, WindowInfo? window = null)
        => new(entry, window) { Icon = icons.Get(entry.ExePath) };
    private void OnForeground(nint foreground)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnForeground(foreground)); return; }
        var window = windows.FirstOrDefault(w => w.Handle == foreground || Native.GetAncestor(w.Handle, 3) == Native.GetAncestor(foreground, 3));
        if (window != null) recent[window.Identity] = ++activationOrder;
        UpdateEntries();
    }
    private void QueueRefresh()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(QueueRefresh); return; }
        if (!closing && !debounceTimer.IsEnabled) debounceTimer.Start();
    }
    private async Task RefreshAsync()
    {
        if (scanning || closing || !ready) return;
        scanning = true;
        try { windows = await catalog.ScanAsync(); if (!closing) UpdateEntries(); }
        catch (Exception error) { store.Log("scan", error); }
        finally { scanning = false; }
    }
    private void UpdateEntries()
    {
        if (!ready || closing) return;
        bool changed = false;
        foreach (var entry in Entries.ToArray())
        {
            WindowInfo? match;
            if (entry.IsProgram) match = WindowRules.SelectProgram(entry.Config, windows, recent);
            else
            {
                match = windows.FirstOrDefault(w => w.Identity == entry.Window?.Identity);
                if (match != null && WindowRules.WordDocumentReturnedHome(entry.Config, match)) { Entries.Remove(entry); changed = true; continue; }
                if (match == null && entry.Window is { } previous)
                {
                    if (!Native.SameWindow(previous.Identity)) { Entries.Remove(entry); changed = true; continue; }
                    match = previous;
                }
                if (match != null && match.Title != entry.Config.LastTitle) changed = true;
            }
            entry.Update(match, match != null && Native.IsForeground(match.Handle));
        }
        EmptyHint.Visibility = Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var identities = windows.Select(w => w.Identity).ToHashSet();
        foreach (var key in recent.Keys.Where(k => !identities.Contains(k)).ToArray()) recent.Remove(key);
        if (changed) ScheduleSave();
    }
    private async void Row_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RailEntry entry }) await ActivateEntry(entry);
    }
    private async Task ActivateEntry(RailEntry entry)
    {
        if (entry.Launching) return;
        long request = ++requestVersion;
        launchCancellation?.Cancel(); switcher.Cancel();
        if (!entry.IsProgram)
        {
            if (entry.Window != null) await ActivateWindow(entry.Window);
            return;
        }
        using var cancellation = new CancellationTokenSource(); launchCancellation = cancellation;
        uint input = Native.InputStamp();
        entry.Launching = true; entry.Refresh();
        try
        {
            await RefreshAsync();
            if (request != requestVersion || cancellation.IsCancellationRequested) return;
            var target = WindowRules.SelectProgram(entry.Config, windows, recent);
            if (target != null) { await ActivateWindow(target); return; }
            if (windows.Any(w => WindowRules.SamePath(w.ExePath, entry.Config.ExePath) && !w.OnCurrentDesktop)) { Toast("此程序的窗口位于其他虚拟桌面"); return; }
            if (!File.Exists(entry.Config.ExePath)) { Toast("程序路径已失效，请从打开的窗口重新固定"); return; }
            bool running = await Task.Run(() => IsProgramRunning(entry.Config.ExePath));
            if (request != requestVersion || cancellation.IsCancellationRequested) return;
            if (running) { Toast("程序正在运行但没有可见窗口，请先从系统托盘打开"); return; }
            Process.Start(new ProcessStartInfo(entry.Config.ExePath) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(entry.Config.ExePath) ?? "" });
            for (int i = 0; i < 45; i++)
            {
                await Task.Delay(330, cancellation.Token);
                if (input != Native.InputStamp()) return;
                await RefreshAsync();
                target = WindowRules.SelectProgram(entry.Config, windows, recent);
                if (target != null) { await ActivateWindow(target); return; }
            }
            Toast("程序已启动，暂未发现可切换窗口");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { store.Log("launch", error); Toast("未能启动程序，请手动打开后重新固定"); }
        finally { entry.Launching = false; entry.Refresh(); if (ReferenceEquals(launchCancellation, cancellation)) launchCancellation = null; }
    }
    private static bool IsProgramRunning(string path)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
        {
            using (process) { try { if (WindowRules.SamePath(Native.ProcessInfo((uint)process.Id).Path, path)) return true; } catch { } }
        }
        return false;
    }
    private async Task ActivateWindow(WindowInfo window)
    {
        if (!Native.IsWindowVisible(window.Handle)) { Toast("窗口目前不可见，请从程序入口唤起"); return; }
        string? message = await switcher.SwitchAsync(window);
        if (message != null) Toast(message);
        QueueRefresh();
    }
    private void Add_Click(object sender, RoutedEventArgs e) => OpenPicker();
    private void OpenPicker()
    {
        if (!ready) return;
        if (picker != null) { picker.Activate(); return; }
        requestVersion++; launchCancellation?.Cancel(); switcher.Cancel();
        picker = new WindowPicker(catalog, icons, Entries.Where(e => !e.IsProgram && e.Window != null).Select(e => e.Window!.Identity));
        if (inspectable) { picker.ShowInTaskbar = true; picker.Topmost = true; }
        else picker.Owner = this;
        try
        {
            if (picker.ShowDialog() == true)
            {
                int added = 0;
                foreach (var window in picker.Selected)
                {
                    if (!Native.SameWindow(window.Identity)) continue;
                    if (!Entries.Any(e => !e.IsProgram && e.Window?.Identity == window.Identity))
                    {
                        Entries.Add(CreateEntry(new() { Kind = EntryKind.Window, ExePath = window.ExePath, AppName = window.AppName, LastTitle = window.Title }, window)); added++;
                    }
                    if (picker.PinPrograms) PinProgram(window);
                }
                if (added > 0) { EmptyHint.Visibility = Visibility.Collapsed; Toast($"已添加 {added} 个窗口"); }
                ScheduleSave(); QueueRefresh();
            }
        }
        finally { picker = null; }
    }
    private void PinProgram(WindowInfo window)
    {
        if (!WindowRules.CanPin(window)) { Toast("无法识别这个窗口的程序启动入口"); return; }
        if (Entries.Any(e => e.IsProgram && WindowRules.SamePath(e.Config.ExePath, window.ExePath))) return;
        Entries.Add(CreateEntry(new() { Kind = EntryKind.Program, ExePath = window.ExePath, AppName = window.AppName }));
        ScheduleSave();
    }
    private void Row_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RailEntry entry } anchor) return;
        e.Handled = true;
        var menu = new ContextMenu();
        if (entry.IsProgram)
        {
            var matches = windows.Where(w => WindowRules.SamePath(w.ExePath, entry.Config.ExePath) && w.OnCurrentDesktop).ToList();
            foreach (var window in matches) menu.Items.Add(Menu(window.Label, async () => await ActivateWindow(window)));
            if (matches.Count > 0) menu.Items.Add(new Separator());
        }
        else
        {
            var pin = Menu("固定所属程序", () => { if (entry.Window != null) PinProgram(entry.Window); });
            pin.IsEnabled = entry.Window != null && WindowRules.CanPin(entry.Window) && !Entries.Any(p => p.IsProgram && WindowRules.SamePath(p.Config.ExePath, entry.Config.ExePath));
            menu.Items.Add(pin);
        }
        menu.Items.Add(Menu("设置显示别名…", () => Rename(entry)));
        menu.Items.Add(Menu("向上移动", () => MoveEntry(entry, -1), Entries.IndexOf(entry) > 0));
        menu.Items.Add(Menu("向下移动", () => MoveEntry(entry, 1), Entries.IndexOf(entry) < Entries.Count - 1));
        menu.Items.Add(new Separator());
        menu.Items.Add(Menu(entry.IsProgram ? "取消固定程序" : "从窗栏移除", () => { Entries.Remove(entry); UpdateEntries(); ScheduleSave(); }));
        menu.PlacementTarget = anchor; menu.IsOpen = true;
    }
    private MenuItem Menu(string title, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = title, IsEnabled = enabled };
        item.Click += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, action);
        return item;
    }
    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Menu("添加窗口…", OpenPicker));
        var width = new MenuItem { Header = "宽度" };
        foreach (var option in new[] { ("紧凑", 184d), ("标准", 224d), ("宽一些", 300d) })
        {
            var item = Menu(option.Item1, () => { Width = config.Width = option.Item2; ClampPosition(); ScheduleSave(); }); item.IsCheckable = true; item.IsChecked = Math.Abs(Width - option.Item2) < 2; width.Items.Add(item);
        }
        menu.Items.Add(width);
        var background = new MenuItem { Header = "背景透明度" };
        foreach (var option in new[] { ("全透明", 0d), ("轻透", 0.35), ("清晰", 0.7) })
        {
            var item = Menu(option.Item1, () => { config.SurfaceOpacity = option.Item2; ApplyAppearance(); ScheduleSave(); }); item.IsCheckable = true; item.IsChecked = Math.Abs(config.SurfaceOpacity - option.Item2) < 0.01; background.Items.Add(item);
        }
        menu.Items.Add(background);
        var dark = Menu("深色文字（浅色桌面）", () => { config.DarkText = !config.DarkText; ApplyAppearance(); ScheduleSave(); }); dark.IsCheckable = true; dark.IsChecked = config.DarkText; menu.Items.Add(dark);
        menu.Items.Add(new Separator());
        menu.Items.Add(Menu("隐藏到系统托盘", Hide));
        menu.Items.Add(Menu("退出窗栏", () => Application.Current.Shutdown()));
        menu.PlacementTarget = MenuButton; menu.IsOpen = true;
    }
    private void ApplyAppearance()
    {
        Resources["RailText"] = new SolidColorBrush(config.DarkText ? Color.FromRgb(25, 36, 52) : Color.FromRgb(245, 247, 250));
        Resources["LabelShadow"] = new DropShadowEffect { Color = config.DarkText ? Colors.White : Colors.Black, Opacity = 0.9, BlurRadius = 5, ShadowDepth = 1 };
        byte alpha = (byte)Math.Max(1, config.SurfaceOpacity * 255);
        Resources["RowSurface"] = new SolidColorBrush(config.DarkText ? Color.FromArgb(alpha, 239, 245, 251) : Color.FromArgb(alpha, 24, 34, 49));
    }
    private void Rename(RailEntry entry)
    {
        var dialog = new Window { Title = "显示别名 · 窗栏", Width = 380, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, ShowInTaskbar = false, Background = Brushes.White, FontFamily = FontFamily };
        var content = new StackPanel { Margin = new Thickness(22) };
        content.Children.Add(new TextBlock { Text = "为这个入口设置一个容易辨认的名称", Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 12) });
        var text = new TextBox { Text = entry.Config.Alias, MaxLength = 100, Padding = new Thickness(9), FontSize = 14 };
        System.Windows.Automation.AutomationProperties.SetName(text, "显示别名"); content.Children.Add(text);
        content.Children.Add(new TextBlock { Text = "留空则跟随窗口标题。", Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 8, 0, 15) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", Padding = new Thickness(16, 7, 16, 7), IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        var save = new Button { Content = "保存", Padding = new Thickness(16, 7, 16, 7), IsDefault = true };
        save.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(save); content.Children.Add(buttons); dialog.Content = content;
        dialog.Loaded += (_, _) => { text.Focus(); text.SelectAll(); };
        if (dialog.ShowDialog() == true) { entry.Config.Alias = text.Text.Trim(); entry.Refresh(); ScheduleSave(); }
    }
    private void MoveEntry(RailEntry entry, int offset) { int index = Entries.IndexOf(entry); int target = index + offset; if (index >= 0 && target >= 0 && target < Entries.Count) { Entries.Move(index, target); ScheduleSave(); } }
    private void Grip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); ClampPosition(); ScheduleSave(); } catch (InvalidOperationException) { }
    }
    private void Row_MouseDown(object sender, MouseButtonEventArgs e) { dragStart = e.GetPosition(this); dragEntry = (sender as FrameworkElement)?.DataContext as RailEntry; }
    private void Row_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || dragEntry == null || sender is not FrameworkElement source) return;
        var now = e.GetPosition(this);
        if (Math.Abs(now.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(now.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var entry = dragEntry; dragEntry = null;
        DragDrop.DoDragDrop(source, new DataObject("WindowRail.Entry", entry.Config.Id.ToString()), DragDropEffects.Move);
    }
    private void Rail_DragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent("WindowRail.Entry") ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; }
    private void Rail_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData("WindowRail.Entry") is not string text || !Guid.TryParse(text, out var id)) return;
        var source = Entries.FirstOrDefault(x => x.Config.Id == id); if (source == null) return;
        var element = e.OriginalSource as DependencyObject;
        while (element != null && element is not Button) element = VisualTreeHelper.GetParent(element);
        int target = element is Button { DataContext: RailEntry entry } ? Entries.IndexOf(entry) : Entries.Count - 1;
        int index = Entries.IndexOf(source);
        if (index >= 0 && target >= 0 && index != target) { Entries.Move(index, target); ScheduleSave(); }
    }
    private void Toast(string message)
    {
        if (closing) return;
        ToastText.Text = message; ToastBorder.Visibility = Visibility.Visible;
        toastTimer.Stop(); toastTimer.Start();
    }
    private void ScheduleSave() { if (!ready || closing) return; saveTimer.Stop(); saveTimer.Start(); }
    private void Persist()
    {
        if (!ready) return;
        config.Entries = Entries.Select(e => e.Config).ToList();
        CapturePosition();
        try { store.Save(config); } catch (Exception error) { store.Log("save", error); Toast("配置未能保存，请检查程序文件夹是否可写"); }
    }
    private void CapturePosition()
    {
        if (hwnd == 0 || !Native.GetWindowRect(hwnd, out var rect)) return;
        var screen = Forms.Screen.FromHandle(hwnd); var area = screen.WorkingArea;
        config.Monitor = screen.DeviceName;
        config.AnchorX = Math.Clamp((rect.Left - area.Left) / (double)Math.Max(1, area.Width - (rect.Right - rect.Left)), 0, 1);
        config.AnchorY = Math.Clamp((rect.Top - area.Top) / (double)Math.Max(1, area.Height - (rect.Bottom - rect.Top)), 0, 1);
    }
    private void RestorePosition()
    {
        var screen = Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == config.Monitor) ?? Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];
        var area = screen.WorkingArea;
        Native.GetWindowRect(hwnd, out var rect);
        int x = area.Left + (int)(Math.Max(0, area.Width - (rect.Right - rect.Left)) * config.AnchorX);
        int y = area.Top + (int)(Math.Max(0, area.Height - (rect.Bottom - rect.Top)) * config.AnchorY);
        positioning = true;
        try { Native.SetWindowPos(hwnd, -1, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE); } finally { positioning = false; }
        UpdateMaxHeight(); ClampPosition();
    }
    private void ClampPosition()
    {
        if (hwnd == 0 || positioning || !Native.GetWindowRect(hwnd, out var rect)) return;
        var area = Forms.Screen.FromHandle(hwnd).WorkingArea;
        int x = Math.Clamp(rect.Left, area.Left, Math.Max(area.Left, area.Right - (rect.Right - rect.Left)));
        int y = Math.Clamp(rect.Top, area.Top, Math.Max(area.Top, area.Bottom - (rect.Bottom - rect.Top)));
        if (x == rect.Left && y == rect.Top) return;
        positioning = true;
        try { Native.SetWindowPos(hwnd, 0, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOZORDER); } finally { positioning = false; }
    }
    private void UpdateMaxHeight() { if (hwnd == 0) return; RowsScroller.MaxHeight = Math.Max(90, Forms.Screen.FromHandle(hwnd).WorkingArea.Height / VisualTreeHelper.GetDpi(this).DpiScaleY - 180); }
    private void OnDisplayChange(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => { UpdateMaxHeight(); ClampPosition(); });
    private void CreateTray()
    {
        System.Drawing.Icon? appIcon = null;
        try { appIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        tray = new Forms.NotifyIcon { Icon = appIcon ?? System.Drawing.SystemIcons.Application, Text = "窗栏 · 双击显示", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示窗栏", null, (_, _) => Dispatcher.BeginInvoke(Reveal));
        menu.Items.Add("添加窗口…", null, (_, _) => Dispatcher.BeginInvoke(() => { Reveal(); OpenPicker(); }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.BeginInvoke(() => Application.Current.Shutdown()));
        tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(Reveal);
    }
    public void Reveal() { if (closing) return; Show(); Topmost = true; ClampPosition(); }
    private void OnClosed(object? sender, EventArgs e)
    {
        Persist(); closing = true;
        launchCancellation?.Cancel(); switcher.Cancel();
        events?.Dispose(); refreshTimer.Stop(); debounceTimer.Stop(); saveTimer.Stop(); toastTimer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplayChange;
        if (tray != null) { tray.Visible = false; tray.Icon?.Dispose(); tray.ContextMenuStrip?.Dispose(); tray.Dispose(); }
    }
}
