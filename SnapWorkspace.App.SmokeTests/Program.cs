using System.IO;
using System.IO.Compression;
using SnapWorkspace.App;
using SnapWorkspace.App.Services;
using SnapWorkspace.Route3;

internal static class Program
{
[STAThread]
private static int Main()
{
var root = Path.Combine(Path.GetTempPath(), $"snapworkspace-support-test-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
try
{
    var diagnostics = new DiagnosticSupportService(root);
    diagnostics.Record("smoke_test", new { success = true });
    var sensitivePath = @"C:\Sensitive\PrivateApp.exe";
    var sensitiveTitle = "Secret document title";
    var workspace = WorkspaceService.Create(
        "Private workspace name",
        BuiltInLayouts.Get("halves"),
        new PhysicalRect(0, 0, 1920, 1080),
        [new WorkspaceApplication
        {
            DisplayName = "Private application",
            Kind = WorkspaceApplicationKind.Window,
            ZoneId = "left",
            WindowIdentity = new WindowIdentity
            {
                ProcessPath = sensitivePath,
                ClassName = "PrivateWindowClass",
                ExactTitle = sensitiveTitle
            },
            Launch = new ApplicationLaunchSpec { ExecutablePath = sensitivePath }
        }]);
    var windows = new[]
    {
        new WindowSnapshot((nint)0x1234, 77, sensitivePath, "PrivateWindowClass", sensitiveTitle,
            new PhysicalRect(0, 0, 960, 1080))
    };

    var defaultBundle = Path.Combine(root, "default.zip");
    diagnostics.Export(defaultBundle, new AppSettings(), null, [workspace], windows);
    using (var archive = ZipFile.OpenRead(defaultBundle))
    {
        RequireEntry(archive, "manifest.json");
        RequireEntry(archive, "settings-summary.json");
        RequireEntry(archive, "route3-capability.json");
        RequireEntry(archive, "workspaces-summary.json");
        Require(archive.GetEntry("application-details.json") is null,
            "默认支持包不应包含 application-details.json。");
        Require(!archive.Entries.Any(entry => entry.FullName.StartsWith("workspace-definitions/", StringComparison.Ordinal)),
            "默认支持包不应包含完整工作区定义。");
        var content = string.Join("\n", archive.Entries.Select(ReadEntry));
        Require(!content.Contains(sensitivePath, StringComparison.Ordinal), "默认支持包泄露了应用路径。");
        Require(!content.Contains(sensitiveTitle, StringComparison.Ordinal), "默认支持包泄露了窗口标题。");
        Require(!content.Contains("Private workspace name", StringComparison.Ordinal), "默认支持包泄露了工作区名称。");
    }

    var detailedBundle = Path.Combine(root, "detailed.zip");
    diagnostics.Export(detailedBundle, new AppSettings
    {
        SupportBundleIncludeApplicationDetails = true,
        SupportBundleIncludeWorkspaceDefinitions = true
    }, null, [workspace], windows);
    using (var archive = ZipFile.OpenRead(detailedBundle))
    {
        RequireEntry(archive, "application-details.json");
        Require(archive.Entries.Any(entry => entry.FullName.StartsWith("workspace-definitions/", StringComparison.Ordinal)),
            "显式选择完整定义后仍未写入工作区 JSON。");
        var content = string.Join("\n", archive.Entries.Select(ReadEntry));
        Require(content.Contains("PrivateApp.exe", StringComparison.Ordinal), "显式选择详细信息后缺少应用路径。");
        Require(content.Contains(sensitiveTitle, StringComparison.Ordinal), "显式选择详细信息后缺少窗口标题。");
    }

    diagnostics.Clear();
    Require(!Directory.EnumerateFiles(diagnostics.LogRootPath, "*.jsonl").Any(), "清除诊断事件失败。");
    var trayHostTested = !string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);
    if (trayHostTested)
    {
        using var startupHost = new StartupTrayHost(
            new AppSettings { EnableTrayIcon = true, GlobalHotkeyEnabled = false },
             new WorkspaceRepository(Path.Combine(root, "tray-host-data")),
             _ => Task.CompletedTask,
             () => { },
             () => { },
             _ => Task.CompletedTask,
             () => { });
        Require(System.Windows.Application.Current?.MainWindow is null, "轻量托盘宿主不应构造完整 WPF 主窗口。");
    }
    Console.WriteLine(trayHostTested
        ? "PASS: 支持包隐私边界、结构、清理行为及轻量托盘宿主均符合预期。"
        : "PASS: 支持包隐私边界、结构和清理行为符合预期；无桌面 CI 跳过通知区域宿主。" );
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"FAIL: {exception.Message}");
    return 1;
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}
}

static void RequireEntry(ZipArchive archive, string name) =>
    Require(archive.GetEntry(name) is not null, $"支持包缺少 {name}。");

static string ReadEntry(ZipArchiveEntry entry)
{
    using var reader = new StreamReader(entry.Open());
    return reader.ReadToEnd();
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}
}
