namespace SnapWorkspace.Route3;

public static class BuiltInLayouts
{
    public static IReadOnlyList<SnapLayoutDefinition> All { get; } =
    [
        Create("halves", "左右二分",
            Zone("left", 0, 0, 0.5, 1),
            Zone("right", 0.5, 0, 0.5, 1)),

        Create("wide-left", "左侧 2/3 + 右侧 1/3",
            Zone("main", 0, 0, 2d / 3d, 1),
            Zone("side", 2d / 3d, 0, 1d / 3d, 1)),

        Create("wide-right", "左侧 1/3 + 右侧 2/3",
            Zone("side", 0, 0, 1d / 3d, 1),
            Zone("main", 1d / 3d, 0, 2d / 3d, 1)),

        Create("thirds", "三列等分",
            Zone("left", 0, 0, 1d / 3d, 1),
            Zone("center", 1d / 3d, 0, 1d / 3d, 1),
            Zone("right", 2d / 3d, 0, 1d / 3d, 1)),

        Create("main-left-stack", "左侧主窗 + 右侧双栏",
            Zone("main", 0, 0, 2d / 3d, 1),
            Zone("side-top", 2d / 3d, 0, 1d / 3d, 0.5),
            Zone("side-bottom", 2d / 3d, 0.5, 1d / 3d, 0.5)),

        Create("main-right-stack", "右侧主窗 + 左侧双栏",
            Zone("side-top", 0, 0, 1d / 3d, 0.5),
            Zone("side-bottom", 0, 0.5, 1d / 3d, 0.5),
            Zone("main", 1d / 3d, 0, 2d / 3d, 1)),

        Create("half-left-stack", "左半单栏 + 右半双栏",
            Zone("main", 0, 0, 0.5, 1),
            Zone("side-top", 0.5, 0, 0.5, 0.5),
            Zone("side-bottom", 0.5, 0.5, 0.5, 0.5)),

        Create("half-right-stack", "右半单栏 + 左半双栏",
            Zone("side-top", 0, 0, 0.5, 0.5),
            Zone("side-bottom", 0, 0.5, 0.5, 0.5),
            Zone("main", 0.5, 0, 0.5, 1)),

        Create("center-focus", "中间主窗 + 两侧窄栏",
            Zone("left", 0, 0, 0.25, 1),
            Zone("main", 0.25, 0, 0.5, 1),
            Zone("right", 0.75, 0, 0.25, 1)),

        Create("quarters", "四宫格",
            Zone("top-left", 0, 0, 0.5, 0.5),
            Zone("top-right", 0.5, 0, 0.5, 0.5),
            Zone("bottom-left", 0, 0.5, 0.5, 0.5),
            Zone("bottom-right", 0.5, 0.5, 0.5, 0.5))
    ];

    public static SnapLayoutDefinition Get(string id) =>
        All.FirstOrDefault(layout => string.Equals(layout.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"未知布局：{id}。可用布局：{string.Join(", ", All.Select(layout => layout.Id))}");

    private static SnapLayoutDefinition Create(string id, string displayName, params SnapZone[] zones) =>
        new()
        {
            Id = id,
            DisplayName = displayName,
            Zones = [.. zones]
        };

    private static SnapZone Zone(string id, double x, double y, double width, double height) =>
        new()
        {
            Id = id,
            Rect = new NormalizedRect { X = x, Y = y, Width = width, Height = height }
        };
}
