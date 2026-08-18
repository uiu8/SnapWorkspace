using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using System.IO.Compression;
using SnapWorkspace.Route3;

namespace SnapWorkspace.App.Services;

public sealed class WorkspaceRepository
{
    public WorkspaceRepository(string? dataRootPath = null)
    {
        var dataRoot = dataRootPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SnapWorkspace");
        RootPath = Path.Combine(dataRoot, "workspaces");
        BackupRootPath = Path.Combine(dataRoot, "backups");
    }

    public string RootPath { get; }
    public string BackupRootPath { get; }

    public IReadOnlyList<WorkspaceDefinition> LoadAll()
    {
        Directory.CreateDirectory(RootPath);
        var workspaces = new List<WorkspaceDefinition>();
        foreach (var path in Directory.EnumerateFiles(RootPath, "*.json"))
        {
            try
            {
                workspaces.Add(WorkspaceService.Load(path));
            }
            catch
            {
                // 单个损坏文件不应阻止整个工作区列表启动；文件保留供诊断。
            }
        }

        return workspaces.OrderByDescending(item => item.UpdatedAt).ToList();
    }

    public void Save(WorkspaceDefinition workspace)
    {
        Directory.CreateDirectory(RootPath);
        var finalPath = Path.Combine(RootPath, $"{workspace.Id}.json");
        var temporaryPath = finalPath + ".tmp";
        if (File.Exists(finalPath))
        {
            BackupExisting(finalPath, workspace.Id);
        }
        WorkspaceService.Save(temporaryPath, workspace);
        File.Move(temporaryPath, finalPath, overwrite: true);
    }

    public void Delete(WorkspaceDefinition workspace)
    {
        var path = Path.Combine(RootPath, $"{workspace.Id}.json");
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void ExportAll(string archivePath)
    {
        var workspaces = LoadAll();
        if (workspaces.Count == 0)
        {
            throw new InvalidOperationException("当前没有可导出的工作区。");
        }

        var fullPath = Path.GetFullPath(archivePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + ".tmp";
        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        using (var archive = ZipFile.Open(temporaryPath, ZipArchiveMode.Create))
        {
            var manifest = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
            using (var writer = new StreamWriter(manifest.Open()))
            {
                writer.Write(JsonSerializer.Serialize(new
                {
                    format = "SnapWorkspaceArchive",
                    version = 1,
                    exportedAt = DateTimeOffset.UtcNow,
                    workspaceCount = workspaces.Count
                }, new JsonSerializerOptions { WriteIndented = true }));
            }

            foreach (var workspace in workspaces)
            {
                var tempWorkspacePath = Path.Combine(Path.GetTempPath(), $"snapworkspace-export-{Guid.NewGuid():N}.json");
                try
                {
                    WorkspaceService.Save(tempWorkspacePath, workspace);
                    archive.CreateEntryFromFile(
                        tempWorkspacePath,
                        $"workspaces/{workspace.Id}.json",
                        CompressionLevel.Optimal);
                }
                finally
                {
                    if (File.Exists(tempWorkspacePath)) File.Delete(tempWorkspacePath);
                }
            }
        }

        File.Move(temporaryPath, fullPath, overwrite: true);
    }

    public int Import(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var imported = new List<WorkspaceDefinition>();
        if (string.Equals(Path.GetExtension(fullPath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            imported.Add(WorkspaceService.Load(fullPath));
        }
        else
        {
            using var archive = ZipFile.OpenRead(fullPath);
            var manifest = archive.GetEntry("manifest.json")
                ?? throw new InvalidDataException("备份包缺少 manifest.json。");
            using (var document = JsonDocument.Parse(manifest.Open()))
            {
                if (!document.RootElement.TryGetProperty("format", out var format) ||
                    !string.Equals(format.GetString(), "SnapWorkspaceArchive", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("不是有效的 Snap Workspace 备份包。");
                }
            }

            foreach (var entry in archive.Entries.Where(entry =>
                         entry.FullName.StartsWith("workspaces/", StringComparison.Ordinal) &&
                         entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            {
                var temporaryPath = Path.Combine(Path.GetTempPath(), $"snapworkspace-import-{Guid.NewGuid():N}.json");
                try
                {
                    entry.ExtractToFile(temporaryPath, overwrite: true);
                    imported.Add(WorkspaceService.Load(temporaryPath));
                }
                finally
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
            }
        }

        Directory.CreateDirectory(RootPath);
        var existingIds = LoadAll().Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var workspace in imported)
        {
            if (existingIds.Contains(workspace.Id))
            {
                workspace.Id = Guid.NewGuid().ToString("N");
                workspace.Name += "（导入）";
                workspace.CreatedAt = DateTimeOffset.UtcNow;
            }
            existingIds.Add(workspace.Id);
            Save(workspace);
        }

        return imported.Count;
    }

    private void BackupExisting(string sourcePath, string workspaceId)
    {
        var workspaceBackupRoot = Path.Combine(BackupRootPath, workspaceId);
        Directory.CreateDirectory(workspaceBackupRoot);
        var backupPath = Path.Combine(
            workspaceBackupRoot,
            $"{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}.json");
        File.Copy(sourcePath, backupPath, overwrite: false);

        foreach (var stalePath in Directory.EnumerateFiles(workspaceBackupRoot, "*.json")
                     .OrderByDescending(File.GetCreationTimeUtc)
                     .Skip(20))
        {
            File.Delete(stalePath);
        }
    }
}

public sealed class AppSettings
{
    public string Theme { get; set; } = "Dark";
    public int VerificationDelayMilliseconds { get; set; } = 550;
    public List<CaptureExclusionRule> CaptureExclusions { get; set; } = [];
    public List<CaptureRoleRule> CaptureRoleRules { get; set; } = [];
    public List<string> FavoriteApplicationKeys { get; set; } = [];
    public List<string> RecentApplicationKeys { get; set; } = [];
    public bool EnableTrayIcon { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool RunAtStartup { get; set; }
    public bool StartMinimizedAtLogin { get; set; } = true;
    public bool GlobalHotkeyEnabled { get; set; }
    public List<GlobalShortcutBinding> GlobalShortcuts { get; set; } = [];
    // 0.8 及更早版本的兼容字段；加载时会迁移到 GlobalShortcuts。
    public string GlobalHotkey { get; set; } = "Ctrl+Alt+W";
    public string? QuickWorkspaceId { get; set; }
    public bool DiagnosticLoggingEnabled { get; set; } = true;
    public bool SupportBundleIncludeApplicationDetails { get; set; }
    public bool SupportBundleIncludeWorkspaceDefinitions { get; set; }
}

public enum GlobalShortcutAction
{
    CommandPalette,
    ShowMainWindow,
    SmartCapture,
    NewWorkspace,
    RestoreWorkspace
}

public sealed class GlobalShortcutBinding
{
    public const string CommandPaletteId = "command-palette";
    public const string ShowMainWindowId = "show-main-window";
    public const string SmartCaptureId = "smart-capture";
    public const string NewWorkspaceId = "new-workspace";
    public const string QuickWorkspace1Id = "quick-workspace-1";
    public const string QuickWorkspace2Id = "quick-workspace-2";
    public const string QuickWorkspace3Id = "quick-workspace-3";

    public string Id { get; set; } = string.Empty;
    public GlobalShortcutAction Action { get; set; }
    public bool Enabled { get; set; }
    public string Gesture { get; set; } = string.Empty;
    public string? WorkspaceId { get; set; }

    [JsonIgnore]
    public string DisplayName => Id switch
    {
        CommandPaletteId => "命令面板",
        ShowMainWindowId => "显示主窗口",
        SmartCaptureId => "智能捕捉",
        NewWorkspaceId => "新建工作区",
        QuickWorkspace1Id => "快捷工作区 1",
        QuickWorkspace2Id => "快捷工作区 2",
        QuickWorkspace3Id => "快捷工作区 3",
        _ => "全局快捷键"
    };

    public GlobalShortcutBinding Clone() => new()
    {
        Id = Id,
        Action = Action,
        Enabled = Enabled,
        Gesture = Gesture,
        WorkspaceId = WorkspaceId
    };

    public static List<GlobalShortcutBinding> CreateDefaults() =>
    [
        Create(CommandPaletteId, GlobalShortcutAction.CommandPalette, true, "Ctrl+Alt+Space"),
        Create(ShowMainWindowId, GlobalShortcutAction.ShowMainWindow, false, "Ctrl+Alt+W"),
        Create(SmartCaptureId, GlobalShortcutAction.SmartCapture, false, "Ctrl+Alt+C"),
        Create(NewWorkspaceId, GlobalShortcutAction.NewWorkspace, false, "Ctrl+Alt+N"),
        Create(QuickWorkspace1Id, GlobalShortcutAction.RestoreWorkspace, true, "Ctrl+Alt+1"),
        Create(QuickWorkspace2Id, GlobalShortcutAction.RestoreWorkspace, true, "Ctrl+Alt+2"),
        Create(QuickWorkspace3Id, GlobalShortcutAction.RestoreWorkspace, true, "Ctrl+Alt+3")
    ];

    private static GlobalShortcutBinding Create(
        string id,
        GlobalShortcutAction action,
        bool enabled,
        string gesture) => new()
    {
        Id = id,
        Action = action,
        Enabled = enabled,
        Gesture = gesture
    };
}

public sealed class CaptureRoleRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "应用";
    public string? ExecutablePath { get; set; }
    public string? AppUserModelId { get; set; }
    public CaptureApplicationRole Role { get; set; } = CaptureApplicationRole.Ignore;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string MatchDetails => ExecutablePath ?? AppUserModelId ?? "按显示名称匹配";
    public string RoleDisplayName => Role switch
    {
        CaptureApplicationRole.NativeSnapWindow => "原生吸附",
        CaptureApplicationRole.CompatibilityWindow => "兼容定位",
        CaptureApplicationRole.Background => "后台启动",
        _ => "忽略"
    };
}

public sealed class CaptureExclusionRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "应用";
    public string? ExecutablePath { get; set; }
    public string? AppUserModelId { get; set; }
    public string? ExecutablePattern { get; set; }
    public string? PackageFamilyPattern { get; set; }
    public string? PublisherPattern { get; set; }
    public string? DisplayNamePattern { get; set; }
    public bool IsRecommended { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string MatchDetails => ExecutablePath ?? AppUserModelId ??
        (!string.IsNullOrWhiteSpace(ExecutablePattern) ? $"路径：{ExecutablePattern}" : null) ??
        (!string.IsNullOrWhiteSpace(PackageFamilyPattern) ? $"应用包：{PackageFamilyPattern}" : null) ??
        (!string.IsNullOrWhiteSpace(PublisherPattern) ? $"发布者：{PublisherPattern}" : null) ??
        (!string.IsNullOrWhiteSpace(DisplayNamePattern) ? $"名称：{DisplayNamePattern}" : null) ??
        "按显示名称匹配";
}

public sealed class SettingsRepository
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    static SettingsRepository()
    {
        Options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    }

    public SettingsRepository(string? settingsPath = null)
    {
        SettingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SnapWorkspace",
            "settings.json");
    }

    public string SettingsPath { get; }

    public AppSettings Load()
    {
        try
        {
            var settingsFileExists = File.Exists(SettingsPath);
            var settings = settingsFileExists
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Options) ?? new AppSettings()
                : new AppSettings();
            settings.CaptureExclusions ??= [];
            settings.CaptureRoleRules ??= [];
            settings.FavoriteApplicationKeys ??= [];
            settings.RecentApplicationKeys ??= [];
            settings.GlobalHotkey = string.IsNullOrWhiteSpace(settings.GlobalHotkey)
                ? "Ctrl+Alt+W"
                : settings.GlobalHotkey;
            settings.GlobalShortcuts = NormalizeGlobalShortcuts(
                settings.GlobalShortcuts,
                settingsFileExists ? settings.GlobalHotkey : null,
                settingsFileExists ? settings.QuickWorkspaceId : null,
                settingsFileExists && settings.GlobalHotkeyEnabled);
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, Options));
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }

    internal static List<GlobalShortcutBinding> NormalizeGlobalShortcuts(
        IReadOnlyList<GlobalShortcutBinding>? saved,
        string? legacyGesture = null,
        string? legacyWorkspaceId = null,
        bool legacyEnabled = false)
    {
        var defaults = GlobalShortcutBinding.CreateDefaults();
        if (saved is { Count: > 0 })
        {
            foreach (var template in defaults)
            {
                var existing = saved.FirstOrDefault(item =>
                    string.Equals(item.Id, template.Id, StringComparison.OrdinalIgnoreCase));
                if (existing is null) continue;
                template.Enabled = existing.Enabled;
                template.Gesture = string.IsNullOrWhiteSpace(existing.Gesture)
                    ? string.Empty
                    : existing.Gesture.Trim();
                template.WorkspaceId = existing.WorkspaceId;
            }
            return defaults;
        }

        if (!legacyEnabled)
        {
            defaults.First(item => item.Id == GlobalShortcutBinding.QuickWorkspace1Id).WorkspaceId = legacyWorkspaceId;
            return defaults;
        }

        // 旧版只有一个“恢复快捷工作区”热键。迁移时不擅自启用新增动作。
        foreach (var item in defaults) item.Enabled = false;
        var quick = defaults.First(item => item.Id == GlobalShortcutBinding.QuickWorkspace1Id);
        quick.Enabled = legacyEnabled;
        quick.Gesture = string.IsNullOrWhiteSpace(legacyGesture) ? "Ctrl+Alt+W" : legacyGesture.Trim();
        quick.WorkspaceId = legacyWorkspaceId;
        return defaults;
    }
}
