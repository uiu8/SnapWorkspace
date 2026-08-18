namespace SnapWorkspace.Route3;

public sealed record CompatibilityWindowResult(
    string ApplicationId,
    string DisplayName,
    bool Success,
    PhysicalRect ExpectedBounds,
    PhysicalRect? ActualBounds,
    string Message);

public sealed record CompatibilityLayerResult(
    int EvaluatedWindowCount,
    int OverlappingWindowCount,
    int RaisedWindowCount);

public static class CompatibilityWindowBackend
{
    public static IReadOnlyList<CompatibilityWindowResult> PlaceOnce(
        PhysicalRect workArea,
        IReadOnlyList<CompatibilityWindowAssignment> assignments,
        int tolerancePixels = 12)
    {
        var results = new List<CompatibilityWindowResult>(assignments.Count);
        foreach (var assignment in assignments)
        {
            var expected = assignment.Bounds.ToPhysical(workArea);
            try
            {
                var actual = NativeInterop.PlaceWindowByVisibleBounds(assignment.Hwnd, expected);
                var success = IsNear(expected, actual, tolerancePixels);
                results.Add(new CompatibilityWindowResult(
                    assignment.ApplicationId,
                    assignment.DisplayName,
                    success,
                    expected,
                    actual,
                    success
                        ? "已完成一次兼容定位；该窗口未提交给 SnapWindows。"
                        : "窗口拒绝了部分位置或尺寸要求。"));
            }
            catch (Exception exception)
            {
                results.Add(new CompatibilityWindowResult(
                    assignment.ApplicationId,
                    assignment.DisplayName,
                    false,
                    expected,
                    null,
                    exception.Message));
            }
        }

        return results;
    }

    public static CompatibilityLayerResult RestoreOverlapLayer(
        IReadOnlyList<CompatibilityWindowAssignment> compatibilityAssignments,
        IReadOnlyList<SnapAssignment> nativeAssignments)
    {
        if (compatibilityAssignments.Count == 0 || nativeAssignments.Count == 0)
        {
            return new CompatibilityLayerResult(compatibilityAssignments.Count, 0, 0);
        }

        var nativeBounds = nativeAssignments
            .Where(assignment => NativeInterop.IsWindow(assignment.Hwnd))
            .Select(assignment => TryGetBounds(assignment.Hwnd))
            .Where(bounds => bounds is not null)
            .Select(bounds => bounds!.Value)
            .ToArray();
        var overlapping = 0;
        var raised = 0;
        foreach (var assignment in compatibilityAssignments)
        {
            var bounds = TryGetBounds(assignment.Hwnd);
            if (bounds is null || !nativeBounds.Any(native => Intersects(bounds.Value, native)))
            {
                continue;
            }

            overlapping++;
            if (NativeInterop.RaiseWindowWithoutActivation(assignment.Hwnd))
            {
                raised++;
            }
        }

        return new CompatibilityLayerResult(compatibilityAssignments.Count, overlapping, raised);
    }

    public static async Task<CompatibilityLayerResult> StabilizeColdStartOverlapAsync(
        IReadOnlyList<CompatibilityWindowAssignment> compatibilityAssignments,
        IReadOnlyList<SnapAssignment> nativeAssignments,
        TimeSpan? stabilizationDuration = null,
        CancellationToken cancellationToken = default)
    {
        var nativeBounds = nativeAssignments
            .Where(assignment => NativeInterop.IsWindow(assignment.Hwnd))
            .Select(assignment => TryGetBounds(assignment.Hwnd))
            .Where(bounds => bounds is not null)
            .Select(bounds => bounds!.Value)
            .ToArray();
        var overlapping = compatibilityAssignments
            .Where(assignment =>
            {
                var bounds = TryGetBounds(assignment.Hwnd);
                return bounds is not null && nativeBounds.Any(native => Intersects(bounds.Value, native));
            })
            .ToArray();
        if (overlapping.Length == 0)
        {
            return new CompatibilityLayerResult(compatibilityAssignments.Count, 0, 0);
        }

        var temporarilyElevated = overlapping
            .Where(assignment => !NativeInterop.IsTopmostWindow(assignment.Hwnd))
            .ToArray();
        var elevatedCount = temporarilyElevated.Count(assignment =>
            NativeInterop.SetTemporaryTopmost(assignment.Hwnd, enabled: true));
        try
        {
            await Task.Delay(
                stabilizationDuration ?? TimeSpan.FromSeconds(2),
                cancellationToken);
        }
        finally
        {
            foreach (var assignment in temporarilyElevated)
            {
                _ = NativeInterop.SetTemporaryTopmost(assignment.Hwnd, enabled: false);
            }
        }

        var final = RestoreOverlapLayer(compatibilityAssignments, nativeAssignments);
        return final with { RaisedWindowCount = Math.Max(final.RaisedWindowCount, elevatedCount) };
    }

    private static PhysicalRect? TryGetBounds(nint hwnd)
    {
        try
        {
            return NativeInterop.GetVisibleWindowBounds(hwnd);
        }
        catch
        {
            return null;
        }
    }

    private static bool Intersects(PhysicalRect first, PhysicalRect second) =>
        first.X < second.Right && first.Right > second.X &&
        first.Y < second.Bottom && first.Bottom > second.Y;

    private static bool IsNear(PhysicalRect expected, PhysicalRect actual, int tolerance) =>
        Math.Abs(expected.X - actual.X) <= tolerance &&
        Math.Abs(expected.Y - actual.Y) <= tolerance &&
        Math.Abs(expected.Width - actual.Width) <= tolerance &&
        Math.Abs(expected.Height - actual.Height) <= tolerance;
}
