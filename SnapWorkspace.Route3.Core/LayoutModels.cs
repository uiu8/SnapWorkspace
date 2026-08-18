using System.Text.Json;
using System.Text.Json.Serialization;

namespace SnapWorkspace.Route3;

public readonly record struct PhysicalRect(int X, int Y, int Width, int Height)
{
    public int Right => checked(X + Width);
    public int Bottom => checked(Y + Height);
    public bool IsValid => Width > 0 && Height > 0;
    public override string ToString() => $"{X},{Y},{Width},{Height}";
}

public sealed class NormalizedRect
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    public PhysicalRect ToPhysical(PhysicalRect workArea)
    {
        var left = workArea.X + (int)Math.Round(workArea.Width * X);
        var top = workArea.Y + (int)Math.Round(workArea.Height * Y);
        var right = workArea.X + (int)Math.Round(workArea.Width * (X + Width));
        var bottom = workArea.Y + (int)Math.Round(workArea.Height * (Y + Height));
        return new PhysicalRect(left, top, right - left, bottom - top);
    }
}

public sealed class SnapZone
{
    public required string Id { get; set; }
    public required NormalizedRect Rect { get; set; }
}

public sealed class SnapLayoutDefinition
{
    public int SchemaVersion { get; set; } = 1;
    public required string Id { get; set; }
    public required string DisplayName { get; set; }
    public required List<SnapZone> Zones { get; set; }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Id))
        {
            errors.Add("布局 Id 不能为空。");
        }

        if (Zones.Count is < 1 or > 12)
        {
            errors.Add("布局必须包含 1 到 12 个区域。");
        }

        var duplicateIds = Zones
            .GroupBy(zone => zone.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);
        foreach (var duplicateId in duplicateIds)
        {
            errors.Add($"区域 Id 重复：{duplicateId}。");
        }

        const double epsilon = 0.000001;
        foreach (var zone in Zones)
        {
            var rect = zone.Rect;
            if (string.IsNullOrWhiteSpace(zone.Id))
            {
                errors.Add("区域 Id 不能为空。");
            }

            if (!double.IsFinite(rect.X) || !double.IsFinite(rect.Y) ||
                !double.IsFinite(rect.Width) || !double.IsFinite(rect.Height))
            {
                errors.Add($"区域 {zone.Id} 包含非有限数值。");
                continue;
            }

            if (rect.Width <= epsilon || rect.Height <= epsilon)
            {
                errors.Add($"区域 {zone.Id} 的宽高必须大于 0。");
            }

            if (rect.X < -epsilon || rect.Y < -epsilon ||
                rect.X + rect.Width > 1 + epsilon ||
                rect.Y + rect.Height > 1 + epsilon)
            {
                errors.Add($"区域 {zone.Id} 必须位于归一化工作区 [0,1] 内。");
            }
        }

        for (var leftIndex = 0; leftIndex < Zones.Count; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < Zones.Count; rightIndex++)
            {
                if (Overlaps(Zones[leftIndex].Rect, Zones[rightIndex].Rect, epsilon))
                {
                    errors.Add($"区域 {Zones[leftIndex].Id} 与 {Zones[rightIndex].Id} 重叠。");
                }
            }
        }

        return errors;
    }

    public SnapZone GetZone(string zoneId) =>
        Zones.FirstOrDefault(zone => string.Equals(zone.Id, zoneId, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"布局 {Id} 中不存在区域 {zoneId}。");

    private static bool Overlaps(NormalizedRect left, NormalizedRect right, double epsilon)
    {
        var overlapWidth = Math.Min(left.X + left.Width, right.X + right.Width) - Math.Max(left.X, right.X);
        var overlapHeight = Math.Min(left.Y + left.Height, right.Y + right.Height) - Math.Max(left.Y, right.Y);
        return overlapWidth > epsilon && overlapHeight > epsilon;
    }
}

public static class LayoutJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static SnapLayoutDefinition Load(string path)
    {
        var layout = JsonSerializer.Deserialize<SnapLayoutDefinition>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException("布局 JSON 为空。");
        var errors = layout.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        }

        return layout;
    }

    public static void Save(string path, SnapLayoutDefinition layout)
    {
        var errors = layout.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        }

        File.WriteAllText(path, JsonSerializer.Serialize(layout, Options));
    }
}
