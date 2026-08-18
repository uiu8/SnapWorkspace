namespace SnapWorkspace.Route3;

public sealed record NativeSnapLayoutMatch(
    SnapLayoutDefinition Layout,
    IReadOnlyDictionary<string, string> ZoneIdMap,
    double Distance);

/// <summary>
/// The native backend must only receive complete, known Snap Layout models.
/// A successful SnapWindows call and matching rectangles do not, by themselves,
/// prove that Windows created a Snap Group.
/// </summary>
public static class NativeSnapLayoutCatalog
{
    private const double ExactTolerance = 0.002;

    public static NativeSnapLayoutMatch? FindExact(SnapLayoutDefinition layout) =>
        FindBest(layout, requireExact: true, requiredZoneCount: layout.Zones.Count);

    public static NativeSnapLayoutMatch? FindNearest(
        SnapLayoutDefinition layout,
        int? requiredZoneCount = null) =>
        FindBest(layout, requireExact: false, requiredZoneCount: requiredZoneCount);

    public static SnapLayoutDefinition Clone(SnapLayoutDefinition layout) => new()
    {
        SchemaVersion = layout.SchemaVersion,
        Id = layout.Id,
        DisplayName = layout.DisplayName,
        Zones = layout.Zones.Select(zone => new SnapZone
        {
            Id = zone.Id,
            Rect = new NormalizedRect
            {
                X = zone.Rect.X,
                Y = zone.Rect.Y,
                Width = zone.Rect.Width,
                Height = zone.Rect.Height
            }
        }).ToList()
    };

    private static NativeSnapLayoutMatch? FindBest(
        SnapLayoutDefinition layout,
        bool requireExact,
        int? requiredZoneCount)
    {
        NativeSnapLayoutMatch? best = null;
        foreach (var candidate in BuiltInLayouts.All.Where(candidate =>
                     requiredZoneCount is null || candidate.Zones.Count == requiredZoneCount.Value))
        {
            if (candidate.Zones.Count != layout.Zones.Count) continue;
            var match = Match(layout, candidate);
            if (match is null || (requireExact && match.Distance > ExactTolerance * ExactTolerance))
            {
                continue;
            }

            if (best is null || match.Distance < best.Distance) best = match;
        }

        return best;
    }

    private static NativeSnapLayoutMatch? Match(
        SnapLayoutDefinition source,
        SnapLayoutDefinition candidate)
    {
        var bestDistance = double.MaxValue;
        Dictionary<string, string>? bestMap = null;
        foreach (var permutation in Permutations(candidate.Zones))
        {
            var distance = 0d;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < source.Zones.Count; index++)
            {
                distance += RectangleDistance(source.Zones[index].Rect, permutation[index].Rect);
                map[source.Zones[index].Id] = permutation[index].Id;
            }

            if (distance >= bestDistance) continue;
            bestDistance = distance;
            bestMap = map;
        }

        return bestMap is null
            ? null
            : new NativeSnapLayoutMatch(Clone(candidate), bestMap, bestDistance);
    }

    private static double RectangleDistance(NormalizedRect left, NormalizedRect right)
    {
        static double Square(double value) => value * value;
        return Square(left.X - right.X) +
               Square(left.Y - right.Y) +
               Square(left.Width - right.Width) +
               Square(left.Height - right.Height);
    }

    private static IEnumerable<IReadOnlyList<SnapZone>> Permutations(IReadOnlyList<SnapZone> zones)
    {
        var buffer = zones.ToArray();
        return Enumerate(0);

        IEnumerable<IReadOnlyList<SnapZone>> Enumerate(int index)
        {
            if (index == buffer.Length)
            {
                yield return buffer.ToArray();
                yield break;
            }

            for (var current = index; current < buffer.Length; current++)
            {
                (buffer[index], buffer[current]) = (buffer[current], buffer[index]);
                foreach (var permutation in Enumerate(index + 1)) yield return permutation;
                (buffer[index], buffer[current]) = (buffer[current], buffer[index]);
            }
        }
    }
}
