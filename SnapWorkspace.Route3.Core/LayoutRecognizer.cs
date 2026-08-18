namespace SnapWorkspace.Route3;

public sealed record LayoutWindowAssignment(
    WindowSnapshot Window,
    string ZoneId,
    double Similarity);

public sealed record LayoutRecognitionResult(
    SnapLayoutDefinition Layout,
    IReadOnlyList<LayoutWindowAssignment> Assignments,
    double Confidence,
    bool IsCustom,
    string Message);

public static class LayoutRecognizer
{
    public static LayoutRecognitionResult Recognize(
        PhysicalRect workArea,
        IReadOnlyList<WindowSnapshot> windows,
        IReadOnlyList<SnapLayoutDefinition>? candidates = null,
        double builtInThreshold = 0.78)
    {
        if (!workArea.IsValid)
        {
            throw new ArgumentException("工作区矩形无效。", nameof(workArea));
        }

        if (windows.Count == 0)
        {
            throw new ArgumentException("至少选择一个窗口。", nameof(windows));
        }

        var duplicate = windows.GroupBy(window => window.Hwnd).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException("捕捉列表包含重复窗口。", nameof(windows));
        }

        var layouts = candidates ?? BuiltInLayouts.All;
        CandidateMatch? best = null;
        foreach (var layout in layouts.Where(layout => layout.Zones.Count >= windows.Count))
        {
            var match = MatchLayout(workArea, windows, layout);
            if (best is null || match.AdjustedScore > best.AdjustedScore)
            {
                best = match;
            }
        }

        if (best is null)
        {
            throw new InvalidOperationException("没有可容纳所选窗口的内置 Snap Layout。");
        }

        if (best.AverageScore >= builtInThreshold)
        {
            return new LayoutRecognitionResult(
                best.Layout,
                best.Assignments,
                best.AverageScore,
                false,
                $"已匹配内置布局“{best.Layout.DisplayName}”，置信度 {best.AverageScore:P0}。");
        }

        return new LayoutRecognitionResult(
            best.Layout,
            best.Assignments,
            best.AverageScore,
            false,
            $"当前窗口没有可靠对应原生布局；已选择最接近的“{best.Layout.DisplayName}”（匹配度 {best.AverageScore:P0}）。请在保存前确认槽位，或把不支持 Snap 的窗口改为兼容定位。");
    }

    private static CandidateMatch MatchLayout(
        PhysicalRect workArea,
        IReadOnlyList<WindowSnapshot> windows,
        SnapLayoutDefinition layout)
    {
        var similarities = new double[windows.Count, layout.Zones.Count];
        for (var windowIndex = 0; windowIndex < windows.Count; windowIndex++)
        {
            var normalizedWindow = NormalizeAndClip(windows[windowIndex].VisibleBounds, workArea);
            for (var zoneIndex = 0; zoneIndex < layout.Zones.Count; zoneIndex++)
            {
                similarities[windowIndex, zoneIndex] = Similarity(
                    normalizedWindow,
                    layout.Zones[zoneIndex].Rect);
            }
        }

        var usedZones = new bool[layout.Zones.Count];
        var currentZones = new int[windows.Count];
        var bestZones = new int[windows.Count];
        var bestTotal = double.NegativeInfinity;

        void Search(int windowIndex, double total)
        {
            if (windowIndex == windows.Count)
            {
                if (total > bestTotal)
                {
                    bestTotal = total;
                    Array.Copy(currentZones, bestZones, currentZones.Length);
                }

                return;
            }

            for (var zoneIndex = 0; zoneIndex < layout.Zones.Count; zoneIndex++)
            {
                if (usedZones[zoneIndex])
                {
                    continue;
                }

                usedZones[zoneIndex] = true;
                currentZones[windowIndex] = zoneIndex;
                Search(windowIndex + 1, total + similarities[windowIndex, zoneIndex]);
                usedZones[zoneIndex] = false;
            }
        }

        Search(0, 0);
        var average = bestTotal / windows.Count;
        var assignments = windows.Select((window, index) =>
        {
            var zoneIndex = bestZones[index];
            return new LayoutWindowAssignment(
                window,
                layout.Zones[zoneIndex].Id,
                similarities[index, zoneIndex]);
        }).ToList();
        var unusedZonePenalty = Math.Max(0, layout.Zones.Count - windows.Count) * 0.02;
        return new CandidateMatch(layout, assignments, average, average - unusedZonePenalty);
    }

    private static NormalizedRect NormalizeAndClip(PhysicalRect window, PhysicalRect workArea)
    {
        var left = Math.Clamp(window.X, workArea.X, workArea.Right);
        var top = Math.Clamp(window.Y, workArea.Y, workArea.Bottom);
        var right = Math.Clamp(window.Right, workArea.X, workArea.Right);
        var bottom = Math.Clamp(window.Bottom, workArea.Y, workArea.Bottom);
        if (right <= left || bottom <= top)
        {
            throw new InvalidOperationException("窗口不在当前主显示器工作区内。");
        }

        return new NormalizedRect
        {
            X = Round((double)(left - workArea.X) / workArea.Width),
            Y = Round((double)(top - workArea.Y) / workArea.Height),
            Width = Round((double)(right - left) / workArea.Width),
            Height = Round((double)(bottom - top) / workArea.Height)
        };
    }

    private static double Similarity(NormalizedRect left, NormalizedRect right)
    {
        var intersectionLeft = Math.Max(left.X, right.X);
        var intersectionTop = Math.Max(left.Y, right.Y);
        var intersectionRight = Math.Min(left.X + left.Width, right.X + right.Width);
        var intersectionBottom = Math.Min(left.Y + left.Height, right.Y + right.Height);
        var intersection = Math.Max(0, intersectionRight - intersectionLeft) *
                           Math.Max(0, intersectionBottom - intersectionTop);
        var union = left.Width * left.Height + right.Width * right.Height - intersection;
        var iou = union <= 0 ? 0 : intersection / union;

        var edgeError = (
            Math.Abs(left.X - right.X) +
            Math.Abs(left.Y - right.Y) +
            Math.Abs(left.X + left.Width - right.X - right.Width) +
            Math.Abs(left.Y + left.Height - right.Y - right.Height)) / 4;
        var edgeScore = Math.Clamp(1 - edgeError * 2, 0, 1);
        return iou * 0.65 + edgeScore * 0.35;
    }

    private static double Round(double value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);

    private sealed record CandidateMatch(
        SnapLayoutDefinition Layout,
        IReadOnlyList<LayoutWindowAssignment> Assignments,
        double AverageScore,
        double AdjustedScore);
}
