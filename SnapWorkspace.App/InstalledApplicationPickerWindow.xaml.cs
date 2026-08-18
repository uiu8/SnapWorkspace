using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using SnapWorkspace.App.Services;

namespace SnapWorkspace.App;

public partial class InstalledApplicationPickerWindow : Window
{
    private readonly IReadOnlyList<InstalledApplicationEntry> _applications;
    private readonly HashSet<string> _favorites;
    private readonly HashSet<string> _recent;
    private readonly ObservableCollection<InstalledApplicationPickerRow> _visibleRows = [];

    public InstalledApplicationPickerWindow(
        IReadOnlyList<InstalledApplicationEntry> applications,
        IEnumerable<string> favorites,
        IEnumerable<string> recent)
    {
        InitializeComponent();
        _applications = applications;
        _favorites = favorites.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _recent = recent.ToHashSet(StringComparer.OrdinalIgnoreCase);
        ApplicationList.ItemsSource = _visibleRows;
        RefreshResults();
    }

    public InstalledApplicationEntry? SelectedApplication { get; private set; }
    public IReadOnlyList<string> FavoriteKeys => _favorites.OrderBy(value => value).ToList();

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => RefreshResults();
    private void FavoritesOnlyToggle_Click(object sender, RoutedEventArgs e) => RefreshResults();

    private void RefreshResults()
    {
        var query = SearchBox?.Text?.Trim() ?? string.Empty;
        var onlyFavorites = FavoritesOnlyToggle?.IsChecked == true;
        var results = _applications
            .Where(application => (!onlyFavorites || _favorites.Contains(application.IdentityKey)) &&
                                  (string.IsNullOrWhiteSpace(query) ||
                                   application.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                                   application.Details.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(application => _favorites.Contains(application.IdentityKey))
            .ThenByDescending(application => _recent.Contains(application.IdentityKey))
            .ThenBy(application => application.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        _visibleRows.Clear();
        foreach (var application in results)
        {
            _visibleRows.Add(new InstalledApplicationPickerRow(application, _favorites.Contains(application.IdentityKey)));
        }

        if (ResultCountText is not null)
        {
            ResultCountText.Text = $"{results.Count} 个应用 · 收藏优先，其次最近使用";
        }
    }

    private void FavoriteApplication_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not InstalledApplicationPickerRow row) return;
        if (!_favorites.Add(row.Entry.IdentityKey)) _favorites.Remove(row.Entry.IdentityKey);
        row.IsFavorite = _favorites.Contains(row.Entry.IdentityKey);
        if (FavoritesOnlyToggle.IsChecked == true) RefreshResults();
    }

    private void ApplicationList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ChooseSelected();
    private void Choose_Click(object sender, RoutedEventArgs e) => ChooseSelected();

    private void ChooseSelected()
    {
        if (ApplicationList.SelectedItem is not InstalledApplicationPickerRow row) return;
        SelectedApplication = row.Entry;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    internal bool RunRegressionScenario()
    {
        if (_applications.Count == 0) return false;
        var initialCount = _visibleRows.Count;
        var sample = _applications[0];
        SearchBox.Text = sample.DisplayName;
        RefreshResults();
        var searchPassed = _visibleRows.Any(row => row.Entry.IdentityKey == sample.IdentityKey);
        SearchBox.Text = string.Empty;
        _favorites.Add(sample.IdentityKey);
        FavoritesOnlyToggle.IsChecked = true;
        RefreshResults();
        var favoritesPassed = _visibleRows.Count > 0 &&
                              _visibleRows.All(row => _favorites.Contains(row.Entry.IdentityKey));
        FavoritesOnlyToggle.IsChecked = false;
        RefreshResults();
        return initialCount == _applications.Count && searchPassed && favoritesPassed && _visibleRows.Count == _applications.Count;
    }
}

public sealed class InstalledApplicationPickerRow : INotifyPropertyChanged
{
    private bool _isFavorite;

    public InstalledApplicationPickerRow(InstalledApplicationEntry entry, bool isFavorite)
    {
        Entry = entry;
        _isFavorite = isFavorite;
    }

    public InstalledApplicationEntry Entry { get; }
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value) return;
            _isFavorite = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StarGlyph));
        }
    }
    public string StarGlyph => IsFavorite ? "★" : "☆";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
