using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SnapWorkspace.Route3;

public sealed class ShellSnapBackendOptions
{
    public bool RequireKnownShellBinary { get; init; } = true;
    public int GeometryTolerancePixels { get; init; } = 12;
    public int MaximumWindowsPerSubmission { get; init; } = 4;
}

public sealed record SnapCapability(
    bool Supported,
    bool IsKnownShellBinary,
    bool HasManager2,
    bool HasManager3,
    bool HasManager4,
    uint? MaximumSnappedWindowsOnLongAxis,
    uint? MaximumSnappedWindowsOnShortAxis,
    int EffectiveMaximumWindowsPerSubmission,
    string RuntimeClass,
    string ShellBinaryPath,
    string? ShellBinaryVersion,
    string? ShellBinarySha256,
    string WindowIdProvider,
    string Reason);

public sealed record SnapAssignment(nint Hwnd, string ZoneId);

public sealed record SnapWindowOutcome(
    nint Hwnd,
    string ZoneId,
    PhysicalRect ExpectedBounds,
    PhysicalRect? ActualVisibleBounds,
    bool GeometryVerified,
    string? Error);

public sealed record SnapOperationResult(
    bool Success,
    bool FallbackRecommended,
    int HResult,
    string Message,
    IReadOnlyList<SnapWindowOutcome> Windows);

public sealed class ShellSnapBackend
{
    private const string RuntimeClassName = "WindowsUdk.UI.Shell.SnapLayoutManager";
    private const string KnownShellCommonSha256 =
        "6F48A36E81DAA0A23AB5CE89F3BBC0B2ADE13BCD24B82C837300C25262675741";

    private static readonly Guid IidManager3 = new("2de3f20f-38ae-50d4-b853-b440353091dc");
    private static readonly Guid IidManager4 = new("aa1a92d5-47c3-5fb2-a5ee-f5a7c195527b");
    private static readonly Guid IidManager2 = new("73c766f2-7839-5244-88c5-0cb4a47c6cd6");
    private static readonly Guid IidManager = new("fa97dbb2-c98b-53aa-83db-6fd7b3e716eb");
    private static readonly Guid IidMaximumPolicy = new("7c7c6db8-4514-58f0-be56-435597312ce9");
    private static readonly Guid IidStatics = new("3d7594b4-9974-547f-bd9a-9b404e98d699");
    private static readonly Guid IidStatics2 = new("8afd7659-7b86-5a2f-95fe-bd4dabea8eb1");

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int IsSupportedAbi(nint self, out byte supported);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CreateForWorkAreaRectAbi(
        nint self,
        NativeInterop.WinRtRect workArea,
        out nint manager);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int SnapWindowsAbi(
        nint self,
        uint windowIdCount,
        nint windowIds,
        uint rectCount,
        nint rects);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetObjectAbi(nint self, out nint value);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetUInt32Abi(nint self, out uint value);

    private readonly ShellSnapBackendOptions _options;
    private readonly IWindowIdProvider _windowIdProvider;

    public ShellSnapBackend(
        ShellSnapBackendOptions? options = null,
        IWindowIdProvider? windowIdProvider = null)
    {
        _options = options ?? new ShellSnapBackendOptions();
        _windowIdProvider = windowIdProvider ?? new CurrentBuildHwndWindowIdProvider();
    }

    public static PhysicalRect GetPrimaryWorkArea() => NativeInterop.GetPrimaryWorkArea();

    public static IDisposable EnterShellThreadContext() => new ShellThreadContext();

    public SnapCapability Probe()
    {
        var binary = InspectShellBinary();
        if (_options.RequireKnownShellBinary && !binary.IsKnown)
        {
            return Capability(
                supported: false,
                binary,
                hasManager2: false,
                hasManager3: false,
                hasManager4: false,
                maximumLongAxis: null,
                maximumShortAxis: null,
                reason: "Shell 二进制不在已验证基线中，建议回退路线二。");
        }

        try
        {
            using var dpi = new NativeInterop.DpiAwarenessScope();
            using var apartment = new NativeInterop.RoApartmentScope();
            using var className = new NativeInterop.HString(RuntimeClassName);

            var staticsIid = IidStatics;
            var staticsHr = NativeInterop.RoGetActivationFactory(
                className.Value,
                ref staticsIid,
                out var staticsValue);
            if (staticsHr < 0)
            {
                return Capability(false, binary, false, false, false, null, null, $"激活 statics 失败：{Hr(staticsHr)}。");
            }

            using var statics = new NativeInterop.ComPtr(staticsValue);
            var isSupported = Marshal.GetDelegateForFunctionPointer<IsSupportedAbi>(
                NativeInterop.VtableSlot(statics.Value, 6));
            var supportedHr = isSupported(statics.Value, out var supported);
            if (supportedHr < 0 || supported == 0)
            {
                return Capability(false, binary, false, false, false, null, null, $"IsSupported={Hr(supportedHr)}, value={supported}。");
            }

            using var manager = CreateManager(className.Value, NativeInterop.GetPrimaryWorkArea(), out var createHr);
            if (createHr < 0 || manager.IsNull)
            {
                return Capability(false, binary, false, false, false, null, null, $"CreateForWorkAreaRect 失败：{Hr(createHr)}。");
            }

            var (maximumLongAxis, maximumShortAxis) = ReadMaximumPolicy(manager.Value);
            var iid2 = IidManager2;
            var qi2Hr = Marshal.QueryInterface(manager.Value, in iid2, out var manager2Value);
            using var manager2 = new NativeInterop.ComPtr(manager2Value);
            var iid3 = IidManager3;
            var qi3Hr = Marshal.QueryInterface(manager.Value, in iid3, out var manager3Value);
            using var manager3 = new NativeInterop.ComPtr(manager3Value);
            var iid4 = IidManager4;
            var qi4Hr = Marshal.QueryInterface(manager.Value, in iid4, out var manager4Value);
            using var manager4 = new NativeInterop.ComPtr(manager4Value);

            var hasManager2 = qi2Hr >= 0 && !manager2.IsNull;
            var hasManager3 = qi3Hr >= 0 && !manager3.IsNull;
            var hasManager4 = qi4Hr >= 0 && !manager4.IsNull;
            return Capability(
                hasManager2 && hasManager3,
                binary,
                hasManager2,
                hasManager3,
                hasManager4,
                maximumLongAxis,
                maximumShortAxis,
                hasManager2 && hasManager3
                    ? "路线三能力探测通过。"
                    : $"管理器接口不可用：QI2={Hr(qi2Hr)}, QI3={Hr(qi3Hr)}。");
        }
        catch (Exception exception)
        {
            return Capability(false, binary, false, false, false, null, null, exception.Message);
        }
    }

    public SnapOperationResult Snap(
        SnapLayoutDefinition layout,
        PhysicalRect workArea,
        IReadOnlyList<SnapAssignment> assignments)
    {
        var layoutErrors = layout.Validate();
        if (layoutErrors.Count > 0)
        {
            return Failure(unchecked((int)0x80070057), string.Join(" ", layoutErrors));
        }

        if (NativeSnapLayoutCatalog.FindExact(layout) is null)
        {
            return Failure(
                unchecked((int)0x80070032),
                "该几何布局不属于当前已验证的 Windows Snap Layout 模型。" +
                "已阻止调用 SnapWindows，避免窗口看似到位但实际脱离 Snap Group；" +
                "请改用原生布局模型，或将应用明确设为兼容定位。");
        }

        if (!workArea.IsValid)
        {
            return Failure(unchecked((int)0x80070057), "工作区矩形无效。");
        }

        if (assignments.Count == 0)
        {
            return Failure(unchecked((int)0x80070057), "至少需要一个窗口分配。");
        }

        if (assignments.Count > _options.MaximumWindowsPerSubmission)
        {
            return Failure(
                unchecked((int)0x80070032),
                $"当前路线三基线最多一次提交 {_options.MaximumWindowsPerSubmission} 个窗口；" +
                "超过上限会产生部分吸附，因此已阻止调用。");
        }

        if (assignments.Select(item => item.ZoneId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != assignments.Count)
        {
            return Failure(unchecked((int)0x80070057), "同一次提交不能把多个窗口分配到同一个 Zone。");
        }

        var binary = InspectShellBinary();
        if (_options.RequireKnownShellBinary && !binary.IsKnown)
        {
            return Failure(
                unchecked((int)0x80004005),
                "Shell 二进制不在已验证基线中，建议回退路线二。");
        }

        var nativeIds = new NativeInterop.WindowId[assignments.Count];
        var nativeRects = new NativeInterop.WinRtRect[assignments.Count];
        var expectedRects = new PhysicalRect[assignments.Count];

        for (var index = 0; index < assignments.Count; index++)
        {
            var assignment = assignments[index];
            SnapZone zone;
            try
            {
                zone = layout.GetZone(assignment.ZoneId);
            }
            catch (KeyNotFoundException exception)
            {
                return Failure(unchecked((int)0x80070057), exception.Message);
            }

            if (!_windowIdProvider.TryGetWindowId(assignment.Hwnd, out var windowId, out var error))
            {
                return Failure(unchecked((int)0x80070057), error ?? "WindowId 转换失败。");
            }

            expectedRects[index] = zone.Rect.ToPhysical(workArea);
            nativeIds[index] = new NativeInterop.WindowId { Value = windowId };
            nativeRects[index] = NativeInterop.WinRtRect.FromPhysical(expectedRects[index]);
        }

        try
        {
            using var dpi = new NativeInterop.DpiAwarenessScope();
            using var apartment = new NativeInterop.RoApartmentScope();
            using var className = new NativeInterop.HString(RuntimeClassName);
            using var manager = CreateManager(className.Value, workArea, out var createHr);
            if (createHr < 0 || manager.IsNull)
            {
                return Failure(createHr, $"CreateForWorkAreaRect 失败：{Hr(createHr)}。");
            }

            var iid3 = IidManager3;
            var qiHr = Marshal.QueryInterface(manager.Value, in iid3, out var manager3Value);
            using var manager3 = new NativeInterop.ComPtr(manager3Value);
            if (qiHr < 0 || manager3.IsNull)
            {
                return Failure(qiHr, $"ISnapLayoutManager3 不可用：{Hr(qiHr)}。");
            }

            var idsHandle = GCHandle.Alloc(nativeIds, GCHandleType.Pinned);
            var rectsHandle = GCHandle.Alloc(nativeRects, GCHandleType.Pinned);
            int snapHr;
            try
            {
                var snap = Marshal.GetDelegateForFunctionPointer<SnapWindowsAbi>(
                    NativeInterop.VtableSlot(manager3.Value, 6));
                snapHr = snap(
                    manager3.Value,
                    (uint)nativeIds.Length,
                    idsHandle.AddrOfPinnedObject(),
                    (uint)nativeRects.Length,
                    rectsHandle.AddrOfPinnedObject());
            }
            finally
            {
                rectsHandle.Free();
                idsHandle.Free();
            }

            if (snapHr < 0)
            {
                return Failure(snapHr, $"SnapWindows 失败：{Hr(snapHr)}。");
            }

            var outcomes = assignments
                .Select((assignment, index) => new SnapWindowOutcome(
                    assignment.Hwnd,
                    assignment.ZoneId,
                    expectedRects[index],
                    null,
                    false,
                    null))
                .ToArray();
            return new SnapOperationResult(
                true,
                false,
                snapHr,
                $"SnapWindows 已提交 {outcomes.Length} 个窗口；调用方应先运行消息循环，再调用 Verify。",
                outcomes);
        }
        catch (Exception exception)
        {
            return Failure(Marshal.GetHRForException(exception), exception.Message);
        }
    }

    public SnapOperationResult Verify(SnapOperationResult submitted)
    {
        if (submitted.HResult < 0 || submitted.Windows.Count == 0)
        {
            return submitted;
        }

        var outcomes = new List<SnapWindowOutcome>(submitted.Windows.Count);
        foreach (var window in submitted.Windows)
        {
            try
            {
                var actual = NativeInterop.GetVisibleWindowBounds(window.Hwnd);
                outcomes.Add(window with
                {
                    ActualVisibleBounds = actual,
                    GeometryVerified = IsNear(
                        window.ExpectedBounds,
                        actual,
                        _options.GeometryTolerancePixels),
                    Error = null
                });
            }
            catch (Exception exception)
            {
                outcomes.Add(window with
                {
                    ActualVisibleBounds = null,
                    GeometryVerified = false,
                    Error = exception.Message
                });
            }
        }

        var verified = outcomes.All(outcome => outcome.GeometryVerified);
        return submitted with
        {
            Success = verified,
            FallbackRecommended = !verified,
            Message = verified
                ? $"SnapWindows 成功，{outcomes.Count} 个窗口几何验证通过。"
                : "SnapWindows 返回成功，但至少一个窗口未到达目标区域；建议回退或重新提交完整布局。",
            Windows = outcomes
        };
    }

    private static NativeInterop.ComPtr CreateManager(
        nint className,
        PhysicalRect workArea,
        out int createHr)
    {
        var statics2Iid = IidStatics2;
        var factoryHr = NativeInterop.RoGetActivationFactory(
            className,
            ref statics2Iid,
            out var statics2Value);
        if (factoryHr < 0)
        {
            createHr = factoryHr;
            return new NativeInterop.ComPtr(nint.Zero);
        }

        using var statics2 = new NativeInterop.ComPtr(statics2Value);
        var create = Marshal.GetDelegateForFunctionPointer<CreateForWorkAreaRectAbi>(
            NativeInterop.VtableSlot(statics2.Value, 6));
        createHr = create(
            statics2.Value,
            NativeInterop.WinRtRect.FromPhysical(workArea),
            out var managerValue);
        return new NativeInterop.ComPtr(managerValue);
    }

    private SnapCapability Capability(
        bool supported,
        ShellBinaryInfo binary,
        bool hasManager2,
        bool hasManager3,
        bool hasManager4,
        uint? maximumLongAxis,
        uint? maximumShortAxis,
        string reason) =>
        new(
            supported,
            binary.IsKnown,
            hasManager2,
            hasManager3,
            hasManager4,
            maximumLongAxis,
            maximumShortAxis,
            _options.MaximumWindowsPerSubmission,
            RuntimeClassName,
            binary.Path,
            binary.Version,
            binary.Sha256,
            _windowIdProvider.Name,
            reason);

    private static (uint? LongAxis, uint? ShortAxis) ReadMaximumPolicy(nint manager)
    {
        var managerIid = IidManager;
        var managerHr = Marshal.QueryInterface(manager, in managerIid, out var baseManagerValue);
        using var baseManager = new NativeInterop.ComPtr(baseManagerValue);
        if (managerHr < 0 || baseManager.IsNull)
        {
            return (null, null);
        }

        var getPolicy = Marshal.GetDelegateForFunctionPointer<GetObjectAbi>(
            NativeInterop.VtableSlot(baseManager.Value, 8));
        var policyHr = getPolicy(baseManager.Value, out var policyValue);
        using var policy = new NativeInterop.ComPtr(policyValue);
        if (policyHr < 0 || policy.IsNull)
        {
            return (null, null);
        }

        var policyIid = IidMaximumPolicy;
        var queryHr = Marshal.QueryInterface(policy.Value, in policyIid, out var policyInterfaceValue);
        using var policyInterface = new NativeInterop.ComPtr(policyInterfaceValue);
        if (queryHr < 0 || policyInterface.IsNull)
        {
            return (null, null);
        }

        var getLong = Marshal.GetDelegateForFunctionPointer<GetUInt32Abi>(
            NativeInterop.VtableSlot(policyInterface.Value, 6));
        var getShort = Marshal.GetDelegateForFunctionPointer<GetUInt32Abi>(
            NativeInterop.VtableSlot(policyInterface.Value, 7));
        var longHr = getLong(policyInterface.Value, out var longAxis);
        var shortHr = getShort(policyInterface.Value, out var shortAxis);
        return (longHr >= 0 ? longAxis : null, shortHr >= 0 ? shortAxis : null);
    }

    private static ShellBinaryInfo InspectShellBinary()
    {
        var path = Path.Combine(Environment.SystemDirectory, "windowsudk.shellcommon.dll");
        if (!File.Exists(path))
        {
            return new ShellBinaryInfo(path, null, null, false);
        }

        var version = FileVersionInfo.GetVersionInfo(path).FileVersion;
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        return new ShellBinaryInfo(
            path,
            version,
            hash,
            string.Equals(hash, KnownShellCommonSha256, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsNear(PhysicalRect expected, PhysicalRect actual, int tolerance) =>
        Math.Abs(expected.X - actual.X) <= tolerance &&
        Math.Abs(expected.Y - actual.Y) <= tolerance &&
        Math.Abs(expected.Width - actual.Width) <= tolerance * 2 &&
        Math.Abs(expected.Height - actual.Height) <= tolerance * 2;

    private static string Hr(int hr) => $"0x{unchecked((uint)hr):X8}";

    private static SnapOperationResult Failure(int hr, string message) =>
        new(false, true, hr, message, []);

    private sealed record ShellBinaryInfo(
        string Path,
        string? Version,
        string? Sha256,
        bool IsKnown);

    private sealed class ShellThreadContext : IDisposable
    {
        private readonly NativeInterop.DpiAwarenessScope _dpi = new();
        private readonly NativeInterop.RoApartmentScope _apartment = new();

        public void Dispose()
        {
            _apartment.Dispose();
            _dpi.Dispose();
        }
    }
}
