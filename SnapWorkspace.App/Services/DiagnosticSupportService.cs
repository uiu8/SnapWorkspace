using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using SnapWorkspace.Route3;

namespace SnapWorkspace.App.Services;

public sealed class DiagnosticSupportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly object _writeGate = new();

    public DiagnosticSupportService(string? dataRootPath = null)
    {
        DataRootPath = dataRootPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SnapWorkspace");
        LogRootPath = Path.Combine(DataRootPath, "diagnostics");
    }

    public string DataRootPath { get; }
    public string LogRootPath { get; }
    public bool Enabled { get; set; } = true;

    public void Record(string eventName, object? details = null, string level = "information")
    {
        if (!Enabled) return;
        try
        {
            lock (_writeGate)
            {
                Directory.CreateDirectory(LogRootPath);
                DeleteExpiredLogs();
                var path = Path.Combine(LogRootPath, $"snapworkspace-{DateTime.UtcNow:yyyyMMdd}.jsonl");
                var record = new
                {
                    timestamp = DateTimeOffset.UtcNow,
                    level,
                    eventName,
                    processId = Environment.ProcessId,
                    details
                };
                File.AppendAllText(path, JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine);
            }
        }
        catch
        {
            // Diagnostics must never block workspace restore or application startup.
        }
    }

    public void Clear()
    {
        lock (_writeGate)
        {
            if (!Directory.Exists(LogRootPath)) return;
            foreach (var path in Directory.EnumerateFiles(LogRootPath, "*.jsonl"))
            {
                File.Delete(path);
            }
        }
    }

    public string Export(
        string destinationPath,
        AppSettings settings,
        SnapCapability? capability,
        IReadOnlyList<WorkspaceDefinition> workspaces,
        IReadOnlyList<WindowSnapshot> windows)
    {
        var fullPath = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + ".tmp";
        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);

        Record("support_bundle_export_started", new
        {
            settings.SupportBundleIncludeApplicationDetails,
            settings.SupportBundleIncludeWorkspaceDefinitions,
            workspaceCount = workspaces.Count,
            windowCount = windows.Count
        });

        try
        {
            using (var archive = ZipFile.Open(temporaryPath, ZipArchiveMode.Create))
            {
                WriteJson(archive, "manifest.json", new
                {
                    format = "SnapWorkspaceSupportBundle",
                    version = 1,
                    createdAt = DateTimeOffset.UtcNow,
                    appVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
                    os = Environment.OSVersion.VersionString,
                    runtime = Environment.Version.ToString(),
                    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    process64Bit = Environment.Is64BitProcess,
                    privacy = new
                    {
                        applicationDetailsIncluded = settings.SupportBundleIncludeApplicationDetails,
                        workspaceDefinitionsIncluded = settings.SupportBundleIncludeWorkspaceDefinitions
                    }
                });

                WriteJson(archive, "settings-summary.json", new
                {
                    settings.Theme,
                    settings.EnableTrayIcon,
                    settings.CloseToTray,
                    settings.RunAtStartup,
                    settings.StartMinimizedAtLogin,
                    settings.GlobalHotkeyEnabled,
                    diagnosticLoggingEnabled = settings.DiagnosticLoggingEnabled,
                    captureExclusionCount = settings.CaptureExclusions.Count,
                    captureRoleRuleCount = settings.CaptureRoleRules.Count,
                    favoriteApplicationCount = settings.FavoriteApplicationKeys.Count,
                    recentApplicationCount = settings.RecentApplicationKeys.Count
                });

                WriteJson(archive, "route3-capability.json", (object?)capability ?? new
                {
                    supported = false,
                    reason = "Capability probe was not available when the bundle was generated."
                });

                WriteJson(archive, "workspaces-summary.json", workspaces.Select((workspace, index) => new
                {
                    workspace = $"workspace-{index + 1}",
                    workspace.SchemaVersion,
                    layoutId = workspace.Layout.Id,
                    layoutZoneCount = workspace.Layout.Zones.Count,
                    workspace.NativeSnapWindowCount,
                    workspace.CompatibilityWindowCount,
                    workspace.BackgroundApplicationCount,
                    emptyZoneCount = workspace.EmptyZoneIds.Count
                }));

                if (settings.SupportBundleIncludeApplicationDetails)
                {
                    WriteJson(archive, "application-details.json", new
                    {
                        workspaces = workspaces.Select(workspace => new
                        {
                            workspace.Id,
                            workspace.Name,
                            applications = workspace.Applications.Select(application => new
                            {
                                application.Id,
                                application.DisplayName,
                                application.Kind,
                                application.WindowPlacement,
                                application.ZoneId,
                                processPath = application.WindowIdentity?.ProcessPath ?? application.Launch.ExecutablePath,
                                application.WindowIdentity?.AppUserModelId,
                                application.WindowIdentity?.ClassName,
                                title = application.WindowIdentity?.ExactTitle,
                                application.Launch.Arguments,
                                application.Launch.WorkingDirectory
                            })
                        }),
                        currentWindows = windows.Select(window => new
                        {
                            hwnd = $"0x{window.Hwnd.ToInt64():X}",
                            window.ProcessId,
                            window.ProcessPath,
                            window.AppUserModelId,
                            window.ClassName,
                            window.Title,
                            window.VisibleBounds
                        })
                    });
                }

                if (settings.SupportBundleIncludeWorkspaceDefinitions)
                {
                    foreach (var workspace in workspaces)
                    {
                        var tempWorkspacePath = Path.Combine(Path.GetTempPath(), $"snapworkspace-support-{Guid.NewGuid():N}.json");
                        try
                        {
                            WorkspaceService.Save(tempWorkspacePath, workspace);
                            archive.CreateEntryFromFile(
                                tempWorkspacePath,
                                $"workspace-definitions/{workspace.Id}.json",
                                CompressionLevel.Optimal);
                        }
                        finally
                        {
                            if (File.Exists(tempWorkspacePath)) File.Delete(tempWorkspacePath);
                        }
                    }
                }

                lock (_writeGate)
                {
                    if (Directory.Exists(LogRootPath))
                    {
                        foreach (var path in Directory.EnumerateFiles(LogRootPath, "*.jsonl")
                                     .OrderByDescending(File.GetLastWriteTimeUtc)
                                     .Take(7))
                        {
                            archive.CreateEntryFromFile(path, $"diagnostics/{Path.GetFileName(path)}", CompressionLevel.Optimal);
                        }
                    }
                }
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
            Record("support_bundle_export_completed", new { fileSize = new FileInfo(fullPath).Length });
            return fullPath;
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }
    }

    private static void WriteJson(ZipArchive archive, string entryName, object value)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(JsonSerializer.Serialize(value, JsonOptions));
    }

    private void DeleteExpiredLogs()
    {
        if (!Directory.Exists(LogRootPath)) return;
        var cutoff = DateTime.UtcNow.AddDays(-7);
        foreach (var path in Directory.EnumerateFiles(LogRootPath, "*.jsonl"))
        {
            if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
        }
    }
}
