using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SnapWorkspace.Route3;

namespace SnapWorkspace.App;

public sealed record VisualLayoutAssignment(
    WindowChoice Choice,
    ApplicationLaunchSpec Launch,
    WindowMatchSpec Match,
    string? ApplicationId)
{
    public string DisplayName => Choice.DisplayName;
    public string Details => Choice.Details;
}

public partial class VisualLayoutEditorWindow : Window
{
    private const double MinimumZoneSize = 0.12;
    private readonly SnapLayoutDefinition _initialLayout;
    private readonly IReadOnlyList<VisualLayoutAssignment> _availableApplications;
    private readonly Dictionary<string, VisualLayoutAssignment> _assignments;
    private readonly HashSet<string> _selectedZoneIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Border> _zoneBorders = new(StringComparer.OrdinalIgnoreCase);
    private SnapLayoutDefinition _layout;
    private Point _applicationDragStart;

    public SnapLayoutDefinition? ResultLayout { get; private set; }
    public IReadOnlyDictionary<string, VisualLayoutAssignment> ResultAssignments => _assignments;

    public VisualLayoutEditorWindow(
        SnapLayoutDefinition layout,
        IReadOnlyList<VisualLayoutAssignment> availableApplications,
        IReadOnlyDictionary<string, VisualLayoutAssignment> assignments)
    {
        InitializeComponent();
        _availableApplications = availableApplications;
        _assignments = new Dictionary<string, VisualLayoutAssignment>(assignments, StringComparer.OrdinalIgnoreCase);
        var initialMatch = NativeSnapLayoutCatalog.FindExact(layout) ?? NativeSnapLayoutCatalog.FindNearest(layout);
        if (initialMatch is not null)
        {
            RemapAssignments(initialMatch.ZoneIdMap);
            _layout = NativeSnapLayoutCatalog.Clone(initialMatch.Layout);
        }
        else
        {
            var fallback = BuiltInLayouts.Get("halves");
            RemapAssignmentsByOverlap(layout, fallback);
            _layout = NativeSnapLayoutCatalog.Clone(fallback);
        }
        _initialLayout = CloneLayout(_layout);
        LayoutNameBox.Text = _layout.DisplayName;
        ApplicationList.ItemsSource = _availableApplications;
        Loaded += (_, _) => RenderLayout();
    }

    private void RenderLayout()
    {
        ZoneCanvas.Children.Clear();
        _zoneBorders.Clear();
        foreach (var zone in _layout.Zones)
        {
            var selected = _selectedZoneIds.Contains(zone.Id);
            _assignments.TryGetValue(zone.Id, out var assignment);
            var border = new Border
            {
                Tag = zone.Id,
                Background = new SolidColorBrush(selected ? Color.FromRgb(92, 78, 214) : Color.FromRgb(47, 58, 82)),
                BorderBrush = new SolidColorBrush(selected ? Color.FromRgb(178, 169, 255) : Color.FromRgb(111, 122, 153)),
                BorderThickness = new Thickness(selected ? 3 : 1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(3),
                AllowDrop = true,
                Child = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = zone.Id,
                            Foreground = Brushes.White,
                            FontWeight = FontWeights.SemiBold,
                            TextAlignment = TextAlignment.Center
                        },
                        new TextBlock
                        {
                            Text = assignment?.DisplayName ?? "显示桌面",
                            Foreground = new SolidColorBrush(Color.FromRgb(205, 211, 226)),
                            FontSize = 11,
                            Margin = new Thickness(6, 5, 6, 0),
                            MaxWidth = Math.Max(60, zone.Rect.Width * ZoneCanvas.Width - 24),
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            TextAlignment = TextAlignment.Center
                        }
                    }
                }
            };
            border.MouseLeftButtonDown += Zone_MouseLeftButtonDown;
            border.Drop += Zone_Drop;
            _zoneBorders[zone.Id] = border;
            ZoneCanvas.Children.Add(border);
            PositionZoneBorder(zone, border);
        }

        AddVerticalSplitters();
        AddHorizontalSplitters();
        var emptyZones = _layout.Zones.Count(zone => !_assignments.ContainsKey(zone.Id));
        LayoutMetricsText.Text = $"{_layout.Zones.Count}/4 个原生槽位 · {emptyZones} 个空槽位显示桌面";
        LayoutNameBox.Text = _layout.DisplayName;
        EditorStatusText.Text = _selectedZoneIds.Count == 0
            ? "请选择槽位调整模型或分配应用。"
            : $"已选择 {_selectedZoneIds.Count} 个槽位。";
    }

    private static SnapLayoutDefinition CloneLayout(SnapLayoutDefinition layout) => new()
    {
        SchemaVersion = layout.SchemaVersion,
        Id = layout.Id,
        DisplayName = layout.DisplayName,
        Zones = layout.Zones.Select(zone => new SnapZone
        {
            Id = zone.Id,
            Rect = CloneRect(zone.Rect)
        }).ToList()
    };

    private static NormalizedRect CloneRect(NormalizedRect rect) => new()
    {
        X = rect.X,
        Y = rect.Y,
        Width = rect.Width,
        Height = rect.Height
    };

    private void PositionZoneBorder(SnapZone zone, Border border)
    {
        Canvas.SetLeft(border, zone.Rect.X * ZoneCanvas.Width);
        Canvas.SetTop(border, zone.Rect.Y * ZoneCanvas.Height);
        border.Width = zone.Rect.Width * ZoneCanvas.Width;
        border.Height = zone.Rect.Height * ZoneCanvas.Height;
    }

    private void AddVerticalSplitters()
    {
        var positions = _layout.Zones
            .SelectMany(zone => new[] { zone.Rect.X, zone.Rect.X + zone.Rect.Width })
            .Where(value => value > 0.0001 && value < 0.9999)
            .DistinctBy(value => Math.Round(value, 5));
        foreach (var position in positions)
        {
            var left = _layout.Zones.Where(zone => NearlyEqual(zone.Rect.X + zone.Rect.Width, position)).ToList();
            var right = _layout.Zones.Where(zone => NearlyEqual(zone.Rect.X, position)).ToList();
            if (left.Count == 0 || right.Count == 0) continue;
            var overlaps = (from leftZone in left
                            from rightZone in right
                            let overlapTop = Math.Max(leftZone.Rect.Y, rightZone.Rect.Y)
                            let overlapBottom = Math.Min(leftZone.Rect.Y + leftZone.Rect.Height, rightZone.Rect.Y + rightZone.Rect.Height)
                            where overlapBottom - overlapTop > 0.001
                            select (overlapTop, overlapBottom)).ToList();
            if (overlaps.Count == 0) continue;
            var top = overlaps.Min(item => item.overlapTop);
            var bottom = overlaps.Max(item => item.overlapBottom);
            var thumb = CreateSplitter(Cursors.SizeWE);
            thumb.Width = 12;
            thumb.Height = (bottom - top) * ZoneCanvas.Height;
            Canvas.SetLeft(thumb, position * ZoneCanvas.Width - 6);
            Canvas.SetTop(thumb, top * ZoneCanvas.Height);
            var current = position;
            thumb.DragDelta += (_, eventArgs) =>
            {
                var minimum = left.Max(zone => zone.Rect.X + MinimumZoneSize);
                var maximum = right.Min(zone => zone.Rect.X + zone.Rect.Width - MinimumZoneSize);
                var next = Math.Clamp(current + eventArgs.HorizontalChange / ZoneCanvas.Width, minimum, maximum);
                foreach (var zone in left) zone.Rect.Width = next - zone.Rect.X;
                foreach (var zone in right)
                {
                    var edge = zone.Rect.X + zone.Rect.Width;
                    zone.Rect.X = next;
                    zone.Rect.Width = edge - next;
                }
                current = next;
                Canvas.SetLeft(thumb, current * ZoneCanvas.Width - 6);
                RepositionZones();
            };
            thumb.DragCompleted += (_, _) => NormalizeToNearestNativeLayout("比例已吸附到最接近的原生 Snap Layout。");
            ZoneCanvas.Children.Add(thumb);
        }
    }

    private void AddHorizontalSplitters()
    {
        var positions = _layout.Zones
            .SelectMany(zone => new[] { zone.Rect.Y, zone.Rect.Y + zone.Rect.Height })
            .Where(value => value > 0.0001 && value < 0.9999)
            .DistinctBy(value => Math.Round(value, 5));
        foreach (var position in positions)
        {
            var topZones = _layout.Zones.Where(zone => NearlyEqual(zone.Rect.Y + zone.Rect.Height, position)).ToList();
            var bottomZones = _layout.Zones.Where(zone => NearlyEqual(zone.Rect.Y, position)).ToList();
            if (topZones.Count == 0 || bottomZones.Count == 0) continue;
            var overlaps = (from topZone in topZones
                            from bottomZone in bottomZones
                            let overlapLeft = Math.Max(topZone.Rect.X, bottomZone.Rect.X)
                            let overlapRight = Math.Min(topZone.Rect.X + topZone.Rect.Width, bottomZone.Rect.X + bottomZone.Rect.Width)
                            where overlapRight - overlapLeft > 0.001
                            select (overlapLeft, overlapRight)).ToList();
            if (overlaps.Count == 0) continue;
            var left = overlaps.Min(item => item.overlapLeft);
            var right = overlaps.Max(item => item.overlapRight);
            var thumb = CreateSplitter(Cursors.SizeNS);
            thumb.Height = 12;
            thumb.Width = (right - left) * ZoneCanvas.Width;
            Canvas.SetLeft(thumb, left * ZoneCanvas.Width);
            Canvas.SetTop(thumb, position * ZoneCanvas.Height - 6);
            var current = position;
            thumb.DragDelta += (_, eventArgs) =>
            {
                var minimum = topZones.Max(zone => zone.Rect.Y + MinimumZoneSize);
                var maximum = bottomZones.Min(zone => zone.Rect.Y + zone.Rect.Height - MinimumZoneSize);
                var next = Math.Clamp(current + eventArgs.VerticalChange / ZoneCanvas.Height, minimum, maximum);
                foreach (var zone in topZones) zone.Rect.Height = next - zone.Rect.Y;
                foreach (var zone in bottomZones)
                {
                    var edge = zone.Rect.Y + zone.Rect.Height;
                    zone.Rect.Y = next;
                    zone.Rect.Height = edge - next;
                }
                current = next;
                Canvas.SetTop(thumb, current * ZoneCanvas.Height - 6);
                RepositionZones();
            };
            thumb.DragCompleted += (_, _) => NormalizeToNearestNativeLayout("比例已吸附到最接近的原生 Snap Layout。");
            ZoneCanvas.Children.Add(thumb);
        }
    }

    private static Thumb CreateSplitter(Cursor cursor) => new()
    {
        Cursor = cursor,
        Background = new SolidColorBrush(Color.FromArgb(185, 95, 166, 255)),
        Opacity = 0.9
    };

    private void RepositionZones()
    {
        foreach (var zone in _layout.Zones)
        {
            if (_zoneBorders.TryGetValue(zone.Id, out var border)) PositionZoneBorder(zone, border);
        }
    }

    private static bool NearlyEqual(double left, double right) => Math.Abs(left - right) < 0.0001;

    private void Zone_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string zoneId) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) _selectedZoneIds.Clear();
        if (!_selectedZoneIds.Add(zoneId) && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            _selectedZoneIds.Remove(zoneId);
        }
        RenderLayout();
        e.Handled = true;
    }

    private void Zone_Drop(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string zoneId ||
            !e.Data.GetDataPresent(typeof(VisualLayoutAssignment))) return;
        if (e.Data.GetData(typeof(VisualLayoutAssignment)) is VisualLayoutAssignment assignment)
        {
            if (assignment.Choice.Snapshot is not null)
            {
                var duplicateZones = _assignments
                    .Where(pair => pair.Value.Choice.Snapshot?.Hwnd == assignment.Choice.Snapshot.Hwnd)
                    .Select(pair => pair.Key)
                    .Where(existingZoneId => !string.Equals(existingZoneId, zoneId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (var duplicateZoneId in duplicateZones) _assignments.Remove(duplicateZoneId);
            }
            _assignments[zoneId] = assignment;
            _selectedZoneIds.Clear();
            _selectedZoneIds.Add(zoneId);
            RenderLayout();
            e.Handled = true;
        }
    }

    private void ApplicationList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed &&
            ApplicationList.SelectedItem is VisualLayoutAssignment assignment)
        {
            if (_applicationDragStart == default) _applicationDragStart = e.GetPosition(ApplicationList);
            var current = e.GetPosition(ApplicationList);
            if (Math.Abs(current.X - _applicationDragStart.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(current.Y - _applicationDragStart.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                DragDrop.DoDragDrop(ApplicationList, assignment, DragDropEffects.Copy);
                _applicationDragStart = default;
            }
        }
        else
        {
            _applicationDragStart = default;
        }
    }

    private void SplitVertical_Click(object sender, RoutedEventArgs e) => SplitSelected(vertical: true);
    private void SplitHorizontal_Click(object sender, RoutedEventArgs e) => SplitSelected(vertical: false);

    private void SplitSelected(bool vertical)
    {
        if (_selectedZoneIds.Count != 1)
        {
            EditorStatusText.Text = "拆分前请只选择一个槽位。";
            return;
        }
        if (_layout.Zones.Count >= 4)
        {
            EditorStatusText.Text = "路线三当前最多提交 4 个原生 Snap 窗口。";
            return;
        }
        var zone = _layout.GetZone(_selectedZoneIds.Single());
        if ((vertical ? zone.Rect.Width : zone.Rect.Height) < MinimumZoneSize * 2)
        {
            EditorStatusText.Text = "这个槽位已经太窄，无法继续拆分。";
            return;
        }
        var newZone = new SnapZone { Id = NextZoneId(), Rect = CloneRect(zone.Rect) };
        if (vertical)
        {
            zone.Rect.Width /= 2;
            newZone.Rect.X += zone.Rect.Width;
            newZone.Rect.Width = zone.Rect.Width;
        }
        else
        {
            zone.Rect.Height /= 2;
            newZone.Rect.Y += zone.Rect.Height;
            newZone.Rect.Height = zone.Rect.Height;
        }
        _layout.Zones.Add(newZone);
        _selectedZoneIds.Clear();
        _selectedZoneIds.Add(newZone.Id);
        NormalizeToNearestNativeLayout("拆分结果已转换为最接近的原生 Snap Layout。");
    }

    private string NextZoneId()
    {
        for (var number = 1; ; number++)
        {
            var id = $"zone-{number}";
            if (_layout.Zones.All(zone => !string.Equals(zone.Id, id, StringComparison.OrdinalIgnoreCase))) return id;
        }
    }

    private void MergeSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedZoneIds.Count < 2)
        {
            EditorStatusText.Text = "请按住 Ctrl 选择至少两个相邻槽位。";
            return;
        }
        if (_layout.Zones.Count <= 2)
        {
            EditorStatusText.Text = "Windows 没有单槽位 Snap Layout；若只需要一个窗口，请保留二分模型并把另一个槽位留空。";
            return;
        }
        var zones = _layout.Zones.Where(zone => _selectedZoneIds.Contains(zone.Id)).ToList();
        var left = zones.Min(zone => zone.Rect.X);
        var top = zones.Min(zone => zone.Rect.Y);
        var right = zones.Max(zone => zone.Rect.X + zone.Rect.Width);
        var bottom = zones.Max(zone => zone.Rect.Y + zone.Rect.Height);
        var combinedArea = zones.Sum(zone => zone.Rect.Width * zone.Rect.Height);
        if (Math.Abs((right - left) * (bottom - top) - combinedArea) > 0.0001)
        {
            EditorStatusText.Text = "所选槽位不能合并成一个无缺口矩形。";
            return;
        }
        var keeper = zones[0];
        keeper.Rect = new NormalizedRect { X = left, Y = top, Width = right - left, Height = bottom - top };
        var preservedAssignment = zones.Select(zone => _assignments.GetValueOrDefault(zone.Id)).FirstOrDefault(value => value is not null);
        foreach (var zone in zones.Skip(1))
        {
            _layout.Zones.Remove(zone);
            _assignments.Remove(zone.Id);
        }
        if (preservedAssignment is not null) _assignments[keeper.Id] = preservedAssignment;
        _selectedZoneIds.Clear();
        _selectedZoneIds.Add(keeper.Id);
        NormalizeToNearestNativeLayout("合并结果已转换为最接近的原生 Snap Layout。");
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedZoneIds.Count == 0)
        {
            EditorStatusText.Text = "请先选择要留空的槽位。";
            return;
        }
        foreach (var zoneId in _selectedZoneIds) _assignments.Remove(zoneId);
        RenderLayout();
        EditorStatusText.Text = "已留空所选槽位；完整 Snap Layout 不变，恢复后这里仍显示桌面。";
    }

    private void ClearAssignment_Click(object sender, RoutedEventArgs e)
    {
        foreach (var zoneId in _selectedZoneIds) _assignments.Remove(zoneId);
        RenderLayout();
        EditorStatusText.Text = "已清空应用；槽位仍属于完整 Snap Layout。";
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _layout = CloneLayout(_initialLayout);
        _selectedZoneIds.Clear();
        _assignments.Keys.Where(key => _layout.Zones.All(zone => !string.Equals(zone.Id, key, StringComparison.OrdinalIgnoreCase)))
            .ToList().ForEach(key => _assignments.Remove(key));
        RenderLayout();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var errors = _layout.Validate();
        if (_layout.Zones.Count > 4) errors = [.. errors, "路线三当前最多支持 4 个原生槽位。"]; 
        if (errors.Count > 0)
        {
            EditorStatusText.Text = string.Join(" ", errors);
            return;
        }
        var match = NativeSnapLayoutCatalog.FindExact(_layout);
        if (match is null)
        {
            EditorStatusText.Text = "当前几何不属于已验证的原生 Snap Layout，无法应用。请先调整到吸附模型。";
            return;
        }
        ResultLayout = NativeSnapLayoutCatalog.Clone(match.Layout);
        DialogResult = true;
    }

    private void NormalizeToNearestNativeLayout(string status)
    {
        var match = NativeSnapLayoutCatalog.FindNearest(_layout, _layout.Zones.Count);
        if (match is null)
        {
            RenderLayout();
            EditorStatusText.Text = "当前槽位数量没有可用的原生 Snap Layout；操作已取消。";
            return;
        }

        RemapAssignments(match.ZoneIdMap);
        var remappedSelection = _selectedZoneIds
            .Select(id => match.ZoneIdMap.GetValueOrDefault(id))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _layout = NativeSnapLayoutCatalog.Clone(match.Layout);
        _selectedZoneIds.Clear();
        foreach (var id in remappedSelection) _selectedZoneIds.Add(id);
        RenderLayout();
        EditorStatusText.Text = $"{status} 当前模型：{_layout.DisplayName}。";
    }

    private void RemapAssignments(IReadOnlyDictionary<string, string> zoneIdMap)
    {
        var remapped = new Dictionary<string, VisualLayoutAssignment>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _assignments)
        {
            if (zoneIdMap.TryGetValue(pair.Key, out var targetId) && !remapped.ContainsKey(targetId))
            {
                remapped[targetId] = pair.Value;
            }
        }
        _assignments.Clear();
        foreach (var pair in remapped) _assignments[pair.Key] = pair.Value;
    }

    private void RemapAssignmentsByOverlap(SnapLayoutDefinition source, SnapLayoutDefinition target)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var available = target.Zones.ToList();
        foreach (var sourceZone in source.Zones)
        {
            var best = available
                .OrderByDescending(targetZone => OverlapArea(sourceZone.Rect, targetZone.Rect))
                .FirstOrDefault();
            if (best is null) continue;
            map[sourceZone.Id] = best.Id;
            available.Remove(best);
        }
        RemapAssignments(map);
    }

    private static double OverlapArea(NormalizedRect left, NormalizedRect right)
    {
        var width = Math.Max(0, Math.Min(left.X + left.Width, right.X + right.Width) - Math.Max(left.X, right.X));
        var height = Math.Max(0, Math.Min(left.Y + left.Height, right.Y + right.Height) - Math.Max(left.Y, right.Y));
        return width * height;
    }

    internal bool RunRegressionScenario()
    {
        var savedAssignments = _assignments.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        _layout = CloneLayout(_initialLayout);
        _selectedZoneIds.Clear();
        var firstId = _layout.Zones[0].Id;
        _selectedZoneIds.Add(firstId);
        SplitSelected(vertical: true);
        var splitPassed = _layout.Zones.Count == _initialLayout.Zones.Count + 1 &&
                          NativeSnapLayoutCatalog.FindExact(_layout) is not null;
        var createdId = _selectedZoneIds.Single();
        _selectedZoneIds.Clear();
        _selectedZoneIds.Add(firstId);
        _selectedZoneIds.Add(createdId);
        MergeSelected_Click(this, new RoutedEventArgs());
        var mergePassed = _layout.Zones.Count == _initialLayout.Zones.Count &&
                          NativeSnapLayoutCatalog.FindExact(_layout) is not null;
        _selectedZoneIds.Clear();
        _selectedZoneIds.Add(firstId);
        var sampleAssignment = savedAssignments.GetValueOrDefault(firstId) ?? _availableApplications.FirstOrDefault();
        if (sampleAssignment is not null) _assignments[firstId] = sampleAssignment;
        DeleteSelected_Click(this, new RoutedEventArgs());
        var deletePassed = _layout.Zones.Count == _initialLayout.Zones.Count &&
                           !_assignments.ContainsKey(firstId) &&
                           NativeSnapLayoutCatalog.FindExact(_layout) is not null;
        _layout = CloneLayout(_initialLayout);
        _assignments.Clear();
        foreach (var pair in savedAssignments) _assignments[pair.Key] = pair.Value;
        _selectedZoneIds.Clear();
        RenderLayout();
        return splitPassed && mergePassed && deletePassed;
    }

    private void ZoneCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsLoaded) RepositionZones();
    }
}
