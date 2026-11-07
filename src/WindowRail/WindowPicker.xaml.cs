using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace WindowRail;

public partial class WindowPicker : Window
{
    private readonly WindowCatalog catalog;
    private readonly IconCache icons;
    private readonly HashSet<WindowIdentity> existing;
    private List<WindowChoice> choices = new();
    private bool scanning;
    public List<WindowInfo> Selected { get; private set; } = new();
    public bool PinPrograms => PinCheck.IsChecked == true;
    public WindowPicker(WindowCatalog catalog, IconCache icons, IEnumerable<WindowIdentity> added)
    {
        this.catalog = catalog; this.icons = icons; existing = added.ToHashSet();
        InitializeComponent();
        Loaded += async (_, _) => { await RefreshAsync(); SearchBox.Focus(); };
    }
    private async Task RefreshAsync()
    {
        if (scanning) return; scanning = true;
        try
        {
            var selected = choices.Where(c => c.IsSelected).Select(c => c.Info.Identity).ToHashSet();
            var windows = await catalog.ScanAsync();
            choices = windows.Where(w => w.OnCurrentDesktop).OrderBy(w => w.AppName).ThenBy(w => w.Title)
                .Select(w => new WindowChoice(w, existing.Contains(w.Identity), icons.Get(w.ExePath)) { IsSelected = selected.Contains(w.Identity) }).ToList();
            ChoicesList.ItemsSource = choices;
            ApplyFilter(); UpdateCount();
        }
        finally { scanning = false; }
    }
    private void ApplyFilter()
    {
        if (ChoicesList == null || ChoicesList.ItemsSource == null) return;
        string search = SearchBox.Text.Trim();
        var view = CollectionViewSource.GetDefaultView(ChoicesList.ItemsSource);
        view.Filter = item => item is WindowChoice c && (c.Label.Contains(search, StringComparison.OrdinalIgnoreCase) || c.Info.AppName.Contains(search, StringComparison.OrdinalIgnoreCase));
        EmptyText.Visibility = view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = search.Length > 0 ? "没有匹配的窗口" : "暂时没有可添加的窗口，先打开一份文档试试";
    }
    private void UpdateCount() { if (ConfirmButton == null) return; int count = choices.Count(c => c.IsSelected); CountText.Text = count == 0 ? "可多选" : $"已选 {count} 个窗口"; ConfirmButton.IsEnabled = count > 0; }
    private void Search_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void Selection_Changed(object sender, RoutedEventArgs e) => UpdateCount();
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Confirm_Click(object sender, RoutedEventArgs e) { Selected = choices.Where(c => c.IsSelected).Select(c => c.Info).ToList(); DialogResult = true; }
}
