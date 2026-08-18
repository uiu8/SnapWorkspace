using System.Text.Json;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SnapWorkspace.Route3;

namespace SnapWorkspace.Route3.Demo;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        using var shellThreadContext = ShellSnapBackend.EnterShellThreadContext();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var backend = new ShellSnapBackend();
        var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";

        try
        {
            return command switch
            {
                "probe" => Probe(backend),
                "list-layouts" => ListLayouts(),
                "inspect-windows" => InspectWindows(),
                "application-catalog-test" => ApplicationCatalogTest(),
                "capture-current" => CaptureCurrent(
                    args.ElementAtOrDefault(1) ?? "halves",
                    RequireArgument(args, 2, "输出工作区 JSON 路径")),
                "restore" => Restore(
                    backend,
                    RequireArgument(args, 1, "工作区 JSON 路径")),
                "restore-with-launch" => RestoreWithLaunch(
                    backend,
                    RequireArgument(args, 1, "工作区 JSON 路径")),
                "demo" => RunNamedLayout(backend, args.ElementAtOrDefault(1) ?? "halves"),
                "demo-file" => RunLayoutFile(backend, RequireArgument(args, 1, "布局 JSON 路径")),
                "self-test" => RunAllLayouts(backend),
                "workspace-model-test" => WorkspaceModelTest(),
                "window-matching-test" => WindowMatchingTest(),
                "preflight-test" => PreflightTest(),
                "compatibility-placement-test" => CompatibilityPlacementTest(),
                "background-window-test" => BackgroundWindowTest(),
                "layout-recognizer-test" => LayoutRecognizerTest(),
                "background-reuse-test" => BackgroundReuseTest(),
                "background-executable-test" => BackgroundExecutableTest(
                    RequireArgument(args, 1, "可执行文件路径")),
                "background-aumid-test" => BackgroundAumidTest(
                    RequireArgument(args, 1, "AppsFolder AUMID")),
                "background-descendant-test" => BackgroundDescendantTest(),
                "background-child-window-host" => BackgroundChildWindowHost(
                    RequireArgument(args, 1, "测试窗口标题")),
                "workspace-window-lifecycle-test" => WorkspaceWindowLifecycleTest(),
                "runtime-routing-test" => RuntimeRoutingTest(),
                "partial-layout-test" => PartialLayoutTest(backend),
                "custom-layout-test" => CustomLayoutTest(backend),
                "export-layout" => ExportLayout(
                    args.ElementAtOrDefault(1) ?? "halves",
                    RequireArgument(args, 2, "输出 JSON 路径")),
                _ => ShowHelp()
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static int Probe(ShellSnapBackend backend)
    {
        var capability = backend.Probe();
        Console.WriteLine(JsonSerializer.Serialize(
            capability,
            new JsonSerializerOptions { WriteIndented = true }));
        return capability.Supported ? 0 : 2;
    }

    private static int ListLayouts()
    {
        foreach (var layout in BuiltInLayouts.All)
        {
            Console.WriteLine($"{layout.Id,-22} {layout.Zones.Count} zones  {layout.DisplayName}");
            foreach (var zone in layout.Zones)
            {
                var rect = zone.Rect;
                Console.WriteLine(
                    $"  {zone.Id,-14} x={rect.X:0.###} y={rect.Y:0.###} w={rect.Width:0.###} h={rect.Height:0.###}");
            }
        }

        return 0;
    }

    private static int InspectWindows()
    {
        foreach (var window in WindowCatalog.EnumerateCandidates())
        {
            Console.WriteLine(
                $"HWND=0x{window.Hwnd.ToInt64():X} PID={window.ProcessId} " +
                $"Bounds={window.VisibleBounds} Class={window.ClassName}");
            Console.WriteLine($"  {window.ProcessPath ?? "path unavailable"}");
            Console.WriteLine($"  {window.Title}");
        }

        return 0;
    }

    private static int ApplicationCatalogTest()
    {
        var windows = WindowCatalog.EnumerateCandidates()
            .Where(window => window.ProcessId != Environment.ProcessId)
            .ToList();
        var tray = TrayIconCatalog.EnumerateRunningApplications();
        var backgrounds = RunningApplicationCatalog.EnumerateBackgroundCandidates(windows, tray);
        var duplicateKeys = backgrounds
            .GroupBy(item => item.ExecutablePath ?? item.AppUserModelId!, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1);
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var containsSystemComponent = backgrounds.Any(item =>
            item.ExecutablePath?.StartsWith(windowsDirectory, StringComparison.OrdinalIgnoreCase) == true);
        var trayKeys = tray.Select(item => item.ExecutablePath ?? item.AppUserModelId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var backgroundContainsTray = backgrounds.Any(item =>
            trayKeys.Contains(item.ExecutablePath ?? item.AppUserModelId!));
        var passed = !duplicateKeys && !containsSystemComponent && !backgroundContainsTray;
        Console.WriteLine(passed
            ? $"PASS: 识别 {windows.Count} 个任务栏窗口、{tray.Count} 个运行中通知区域应用和 {backgrounds.Count} 个纯后台应用；分类之间无重复。"
            : "FAIL: 应用清单包含重复启动目标、分类重叠或 Windows 系统后台组件。");
        return passed ? 0 : 10;
    }

    private static int CaptureCurrent(string layoutId, string outputPath)
    {
        var layout = BuiltInLayouts.Get(layoutId);
        var workArea = ShellSnapBackend.GetPrimaryWorkArea();
        var captured = WorkspaceService.CaptureCurrent(
            $"Captured {layout.DisplayName}",
            layout,
            workArea,
            WindowCatalog.EnumerateCandidates());
        var path = Path.GetFullPath(outputPath);
        WorkspaceService.Save(path, captured.Workspace);
        Console.WriteLine(
            $"已保存 {captured.Workspace.AssignedWindowCount} 个窗口、" +
            $"{captured.EmptyZones.Count} 个空 Zone：{path}");
        return 0;
    }

    private static int Restore(ShellSnapBackend backend, string workspacePath)
    {
        var workspace = WorkspaceService.Load(Path.GetFullPath(workspacePath));
        var matched = WorkspaceService.MatchExistingWindows(
            workspace,
            WindowCatalog.EnumerateCandidates());
        if (matched.MissingWindows.Count > 0)
        {
            Console.Error.WriteLine("未执行吸附；以下窗口没有匹配成功：");
            foreach (var missing in matched.MissingWindows)
            {
                Console.Error.WriteLine(
                    $"  zone={missing.ZoneId} class={missing.WindowIdentity?.ClassName} title={missing.WindowIdentity?.ExactTitle}");
            }

            return 5;
        }

        var workArea = ShellSnapBackend.GetPrimaryWorkArea();
        var success = true;
        if (matched.Assignments.Count > 0)
        {
            var result = backend.Snap(workspace.Layout, workArea, matched.Assignments);
            PumpMessages(500);
            result = backend.Verify(result);
            PrintResult(workspace.Layout, workArea, result);
            success = result.Success;
        }

        var compatibility = CompatibilityWindowBackend.PlaceOnce(
            workArea,
            matched.CompatibilityAssignments);
        foreach (var result in compatibility)
        {
            Console.WriteLine(
                $"  compatibility={result.DisplayName} expected={result.ExpectedBounds} " +
                $"actual={result.ActualBounds?.ToString() ?? "n/a"} success={result.Success}");
            success &= result.Success;
        }

        return success ? 0 : 3;
    }

    private static int RestoreWithLaunch(ShellSnapBackend backend, string workspacePath)
    {
        var workspace = WorkspaceService.Load(Path.GetFullPath(workspacePath));
        var prepared = WorkspaceLauncher.PrepareAsync(workspace).GetAwaiter().GetResult();
        try
        {
            if (prepared.FailedBackgroundApplications > 0)
            {
                foreach (var outcome in prepared.Outcomes) Console.WriteLine($"  {outcome.DisplayName}: {outcome.Message}");
                return 5;
            }
            if (prepared.MissingWindows.Count > 0)
            {
                Console.WriteLine($"WARNING: {prepared.MissingWindows.Count} 个窗口未就绪，将继续提交其余窗口。");
                foreach (var outcome in prepared.Outcomes.Where(outcome =>
                             prepared.MissingWindows.Any(application => application.Id == outcome.ApplicationId)))
                {
                    Console.WriteLine($"  {outcome.DisplayName}: {outcome.Message}");
                }
            }
            if (prepared.Assignments.Count == 0 && prepared.CompatibilityAssignments.Count == 0)
            {
                return 5;
            }

            var workArea = ShellSnapBackend.GetPrimaryWorkArea();
            var success = true;
            if (prepared.Assignments.Count > 0)
            {
                var snap = backend.Snap(workspace.Layout, workArea, prepared.Assignments);
                PumpMessages(500);
                snap = backend.Verify(snap);
                PrintResult(workspace.Layout, workArea, snap);
                success &= snap.Success;
            }

            var placement = CompatibilityWindowBackend.PlaceOnce(workArea, prepared.CompatibilityAssignments);
            success &= placement.All(result => result.Success);
            CompatibilityLayerResult? layer = null;
            if (prepared.Assignments.Count > 0 && prepared.CompatibilityAssignments.Count > 0)
            {
                layer = CompatibilityWindowBackend.StabilizeColdStartOverlapAsync(
                        prepared.CompatibilityAssignments,
                        prepared.Assignments)
                    .GetAwaiter().GetResult();
            }
            Console.WriteLine($"Compatibility={placement.Count}, overlap={layer?.OverlappingWindowCount ?? 0}, raised={layer?.RaisedWindowCount ?? 0}");
            return success ? 0 : 3;
        }
        finally
        {
            WorkspaceLauncher.ReleaseBackgroundGuard(workspace);
        }
    }

    private static int RunNamedLayout(ShellSnapBackend backend, string layoutId) =>
        RunLayout(backend, BuiltInLayouts.Get(layoutId), keepVisibleMilliseconds: 1200) ? 0 : 3;

    private static int RunLayoutFile(ShellSnapBackend backend, string path) =>
        RunLayout(backend, LayoutJson.Load(Path.GetFullPath(path)), keepVisibleMilliseconds: 1200) ? 0 : 3;

    private static int RunAllLayouts(ShellSnapBackend backend)
    {
        var capability = backend.Probe();
        if (!capability.Supported)
        {
            Console.Error.WriteLine(capability.Reason);
            return 2;
        }

        var failed = new List<string>();
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定当前测试程序路径。");
        foreach (var layout in BuiltInLayouts.All)
        {
            Console.WriteLine();
            Console.WriteLine($"=== {layout.Id}: {layout.DisplayName} ===");
            using var child = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                ArgumentList = { "demo", layout.Id },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = false
            }) ?? throw new InvalidOperationException("无法启动隔离布局测试进程。");
            var standardOutput = child.StandardOutput.ReadToEnd();
            var standardError = child.StandardError.ReadToEnd();
            child.WaitForExit();
            Console.Write(standardOutput);
            if (!string.IsNullOrWhiteSpace(standardError))
            {
                Console.Error.Write(standardError);
            }

            if (child.ExitCode != 0)
            {
                failed.Add(layout.Id);
            }
        }

        Console.WriteLine();
        Console.WriteLine(
            failed.Count == 0
                ? $"PASS: {BuiltInLayouts.All.Count} 种内置布局全部通过。"
                : $"FAIL: {string.Join(", ", failed)}");
        return failed.Count == 0 ? 0 : 4;
    }

    private static int WorkspaceModelTest()
    {
        var layout = BuiltInLayouts.Get("halves");
        var workArea = new PhysicalRect(0, 0, 2000, 1200);
        var original = new[]
        {
            new WindowSnapshot(
                new nint(0x1001),
                101,
                @"C:\Apps\Editor.exe",
                "EditorWindow",
                "Project Alpha",
                new PhysicalRect(0, 0, 1000, 1200)),
            new WindowSnapshot(
                new nint(0x1002),
                102,
                @"C:\Apps\Browser.exe",
                "BrowserWindow",
                "Research",
                new PhysicalRect(1000, 0, 1000, 1200))
        };
        var capture = WorkspaceService.CaptureCurrent(
            "Model self-test",
            layout,
            workArea,
            original);
        if (capture.EmptyZones.Count > 0)
        {
            Console.Error.WriteLine("Capture failed.");
            return 6;
        }

        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"route3-workspace-{Guid.NewGuid():N}.json");
        var legacyPath = Path.Combine(
            Path.GetTempPath(),
            $"route3-workspace-v2-{Guid.NewGuid():N}.json");
        var legacyCustomPath = Path.Combine(
            Path.GetTempPath(),
            $"route3-workspace-v4-custom-{Guid.NewGuid():N}.json");
        try
        {
            WorkspaceService.Save(tempPath, capture.Workspace);
            var loaded = WorkspaceService.Load(tempPath);
            loaded.Applications.Add(new WorkspaceApplication
            {
                DisplayName = "Background Agent",
                Kind = WorkspaceApplicationKind.Background,
                Launch = new ApplicationLaunchSpec
                {
                    ExecutablePath = @"C:\Apps\Agent.exe",
                    Arguments = "--quiet",
                    WorkingDirectory = @"C:\Apps",
                    AlwaysStartNewInstance = true
                }
            });
            for (var index = 2; index <= 6; index++)
            {
                loaded.Applications.Add(new WorkspaceApplication
                {
                    DisplayName = $"Background Agent {index}",
                    Kind = WorkspaceApplicationKind.Background,
                    Launch = new ApplicationLaunchSpec
                    {
                        ExecutablePath = $@"C:\Apps\Agent{index}.exe"
                    }
                });
            }
            var compatibilitySnapshot = new WindowSnapshot(
                new nint(0x1003),
                103,
                @"C:\Apps\LegacyTool.exe",
                "LegacyToolWindow",
                "Legacy tool",
                new PhysicalRect(240, 300, 720, 480));
            loaded.Applications.Add(WorkspaceService.CreateCompatibilityApplication(
                compatibilitySnapshot,
                workArea));
            WorkspaceService.Save(tempPath, loaded);
            loaded = WorkspaceService.Load(tempPath);
            var restarted = new[]
            {
                original[0] with { Hwnd = new nint(0x2001), ProcessId = 201 },
                original[1] with { Hwnd = new nint(0x2002), ProcessId = 202 },
                compatibilitySnapshot with { Hwnd = new nint(0x2003), ProcessId = 203 }
            };
            var matched = WorkspaceService.MatchExistingWindows(loaded, restarted);
            var translatedBounds = WorkspaceService.NormalizeToWorkArea(
                    new PhysicalRect(2300, 150, 720, 480),
                    workArea)
                .ToPhysical(workArea);
            var tinyCaptureRejected = false;
            try
            {
                _ = WorkspaceService.CreateCompatibilityApplication(
                    new WindowSnapshot(
                        new nint(0x7777),
                        777,
                        @"C:\Apps\Tiny.exe",
                        "TinyWindow",
                        "Tiny title bar",
                        new PhysicalRect(0, 0, 350, 56)),
                    workArea);
            }
            catch (InvalidOperationException)
            {
                tinyCaptureRejected = true;
            }
            var passed = matched.MissingWindows.Count == 0 &&
                         matched.Assignments.Count == 2 &&
                         matched.Assignments[0].Hwnd == new nint(0x2001) &&
                         matched.Assignments[1].Hwnd == new nint(0x2002) &&
                         matched.CompatibilityAssignments.Count == 1 &&
                         matched.CompatibilityAssignments[0].Hwnd == new nint(0x2003) &&
                         loaded.SchemaVersion == 5 &&
                         loaded.CompatibilityWindowCount == 1 &&
                         loaded.BackgroundApplicationCount == 6 &&
                         loaded.Applications.Where(application =>
                             application.Kind == WorkspaceApplicationKind.Background).All(application =>
                             application.BackgroundWindowMode == BackgroundWindowMode.Hide) &&
                          loaded.Applications.First(application =>
                              application.DisplayName == "Background Agent").Launch.Arguments == "--quiet" &&
                          translatedBounds.Width == 720 &&
                          translatedBounds.Height == 480 &&
                          translatedBounds.Right == workArea.Right &&
                          tinyCaptureRejected;
            var legacyJson = JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                id = "legacy-v2",
                name = "Legacy workspace",
                layout,
                capturedWorkArea = workArea,
                windows = new[]
                {
                    new WorkspaceWindowSlot
                    {
                        ZoneId = "left",
                        Identity = WorkspaceService.IdentityFrom(original[0])
                    }
                },
                createdAt = DateTimeOffset.UtcNow,
                updatedAt = DateTimeOffset.UtcNow
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            File.WriteAllText(legacyPath, legacyJson);
            var migrated = WorkspaceService.Load(legacyPath);
            var legacyCustomLayout = new SnapLayoutDefinition
            {
                Id = "captured-legacy-test",
                DisplayName = "自定义捕捉 · 1 窗口",
                Zones =
                [
                    new SnapZone
                    {
                        Id = "captured-1",
                        Rect = new NormalizedRect { X = 0.2, Y = 0.15, Width = 0.55, Height = 0.7 }
                    }
                ]
            };
            var legacyCustomJson = JsonSerializer.Serialize(new
            {
                schemaVersion = 4,
                id = "legacy-custom-v4",
                name = "Legacy custom capture",
                layout = legacyCustomLayout,
                capturedWorkArea = workArea,
                applications = new[]
                {
                    new WorkspaceApplication
                    {
                        DisplayName = "Legacy floating window",
                        Kind = WorkspaceApplicationKind.Window,
                        ZoneId = "captured-1",
                        WindowIdentity = WorkspaceService.IdentityFrom(compatibilitySnapshot),
                        Launch = WorkspaceService.LaunchFrom(compatibilitySnapshot)
                    }
                },
                createdAt = DateTimeOffset.UtcNow,
                updatedAt = DateTimeOffset.UtcNow
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            File.WriteAllText(legacyCustomPath, legacyCustomJson);
            var migratedCustom = WorkspaceService.Load(legacyCustomPath);
            passed = passed &&
                     migrated.SchemaVersion == 5 &&
                     migrated.AssignedWindowCount == 1 &&
                     migrated.Applications[0].ZoneId == "left" &&
                     migrated.LegacyWindows is null &&
                     migratedCustom.SchemaVersion == 5 &&
                     migratedCustom.NativeSnapWindowCount == 0 &&
                     migratedCustom.CompatibilityWindowCount == 1 &&
                     migratedCustom.Layout.Id == "halves";
            Console.WriteLine(
                passed
                    ? "PASS: schema v5、旧模型迁移、后台应用、兼容窗口越界平移/异常尺寸拦截、JSON 往返和新 HWND 重匹配通过。"
                    : "FAIL: 工作区模型测试未通过。");
            return passed ? 0 : 6;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            if (File.Exists(legacyPath))
            {
                File.Delete(legacyPath);
            }

            if (File.Exists(legacyCustomPath))
            {
                File.Delete(legacyCustomPath);
            }
        }
    }

    private static int WindowMatchingTest()
    {
        var identity = new WindowIdentity
        {
            ProcessPath = @"C:\Apps\Editor.exe",
            ClassName = "EditorWindow",
            ExactTitle = "Project Alpha - Editor"
        };
        var application = new WorkspaceApplication
        {
            DisplayName = "Alpha editor",
            Kind = WorkspaceApplicationKind.Window,
            ZoneId = "left",
            WindowIdentity = identity,
            Match = new WindowMatchSpec
            {
                ProcessMode = WindowProcessMatchMode.ExactPath,
                TitleMode = WindowTitleMatchMode.Wildcard,
                TitlePattern = "*Alpha*",
                RequireSameWindowClass = true
            },
            Launch = new ApplicationLaunchSpec { ExecutablePath = identity.ProcessPath }
        };
        var right = new WindowSnapshot(
            new nint(0x3001), 301, identity.ProcessPath, "EditorWindow",
            "[modified] Project Alpha - Editor", new PhysicalRect(0, 0, 1000, 1000));
        var wrongDocument = right with
        {
            Hwnd = new nint(0x3002),
            Title = "Project Beta - Editor"
        };
        var rightEvaluation = WorkspaceService.EvaluateMatch(application, right);
        var wrongEvaluation = WorkspaceService.EvaluateMatch(application, wrongDocument);

        application.Match.TitleMode = WindowTitleMatchMode.RegularExpression;
        application.Match.TitlePattern = @"Project\s+Alpha";
        var regexEvaluation = WorkspaceService.EvaluateMatch(application, right);
        var passed = rightEvaluation.IsMatch &&
                     !wrongEvaluation.IsMatch &&
                     regexEvaluation.IsMatch &&
                     rightEvaluation.Score >= 15;
        Console.WriteLine(passed
            ? "PASS: 完整路径、窗口类、通配符和正则标题规则通过。"
            : "FAIL: 自定义窗口匹配规则异常。");
        return passed ? 0 : 11;
    }

    private static int PreflightTest()
    {
        var layout = BuiltInLayouts.Get("halves");
        var application = new WorkspaceApplication
        {
            DisplayName = "Ambiguous editor",
            Kind = WorkspaceApplicationKind.Window,
            ZoneId = "left",
            WindowIdentity = new WindowIdentity
            {
                ProcessPath = @"C:\Apps\Editor.exe",
                ClassName = "EditorWindow",
                ExactTitle = "Editor"
            },
            Launch = new ApplicationLaunchSpec { ExecutablePath = @"C:\Apps\Editor.exe" }
        };
        var workspace = WorkspaceService.Create(
            "Preflight",
            layout,
            new PhysicalRect(0, 0, 1920, 1080),
            new[] { application });
        var candidates = new[]
        {
            new WindowSnapshot(new nint(1), 1, @"C:\Apps\Editor.exe", "EditorWindow", "Editor A", new PhysicalRect(0, 0, 800, 800)),
            new WindowSnapshot(new nint(2), 2, @"C:\Apps\Editor.exe", "EditorWindow", "Editor B", new PhysicalRect(0, 0, 800, 800))
        };
        var ambiguous = WorkspacePreflight.Analyze(workspace, candidates, shellSupported: true);

        application.Launch.ExecutablePath = @"Z:\Missing\Editor.exe";
        application.WindowIdentity.ProcessPath = application.Launch.ExecutablePath;
        var missing = WorkspacePreflight.Analyze(workspace, [], shellSupported: true);
        var backgroundWorkspace = WorkspaceService.Create(
            "Background warning",
            layout,
            new PhysicalRect(0, 0, 1920, 1080),
            new[]
            {
                new WorkspaceApplication
                {
                    DisplayName = "Optional background",
                    Kind = WorkspaceApplicationKind.Background,
                    Launch = new ApplicationLaunchSpec { ExecutablePath = @"Z:\Missing\Agent.exe" }
                }
            });
        var missingBackground = WorkspacePreflight.Analyze(backgroundWorkspace, [], shellSupported: true);
        var passed = ambiguous.CanRestore &&
                     ambiguous.WarningCount == 1 &&
                     !missing.CanRestore &&
                     missing.ErrorCount == 1 &&
                     missingBackground.CanRestore &&
                     missingBackground.WarningCount == 1;
        Console.WriteLine(passed
            ? "PASS: 预检可识别同分候选、必需窗口错误和后台应用提醒。"
            : "FAIL: 恢复前检查结果异常。");
        return passed ? 0 : 12;
    }

    private static int CompatibilityPlacementTest()
    {
        var workArea = ShellSnapBackend.GetPrimaryWorkArea();
        using var nativeForm = new Form
        {
            Text = "Native layer test",
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(workArea.X + 120, workArea.Y + 100, 640, 420),
            ShowInTaskbar = true
        };
        using var form = new Form
        {
            Text = "Compatibility placement test",
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(workArea.X + 40, workArea.Y + 40, 420, 280),
            ShowInTaskbar = true
        };
        nativeForm.Show();
        form.Show();
        PumpMessages(250);
        var bounds = new NormalizedRect { X = 0.12, Y = 0.16, Width = 0.42, Height = 0.48 };
        var results = CompatibilityWindowBackend.PlaceOnce(
            workArea,
            [new CompatibilityWindowAssignment(form.Handle, "compat-test", form.Text, bounds)]);
        PumpMessages(120);
        var result = results.Single();
        nativeForm.Activate();
        nativeForm.BringToFront();
        PumpMessages(120);
        var layer = CompatibilityWindowBackend.StabilizeColdStartOverlapAsync(
                [new CompatibilityWindowAssignment(form.Handle, "compat-test", form.Text, bounds)],
                [new SnapAssignment(nativeForm.Handle, "native-test")],
                TimeSpan.FromMilliseconds(180))
            .GetAwaiter().GetResult();
        PumpMessages(120);
        var passed = result.Success && result.ActualBounds is not null &&
                     layer.OverlappingWindowCount == 1 &&
                     layer.RaisedWindowCount == 1 &&
                     IsWindowAbove(form.Handle, nativeForm.Handle);
        Console.WriteLine(passed
            ? $"PASS: 兼容窗口完成定位，并在与原生窗口重叠时恢复普通前景层级，actual={result.ActualBounds}。"
            : $"FAIL: 兼容定位或重叠层级恢复失败：{result.Message} expected={result.ExpectedBounds} actual={result.ActualBounds} layer={layer}");
        return passed ? 0 : 13;
    }

    private static int BackgroundWindowTest()
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定测试程序路径。");
        using var form = new Form
        {
            Text = "Background visibility test",
            StartPosition = FormStartPosition.Manual,
            ShowInTaskbar = true,
            Bounds = new Rectangle(80, 80, 420, 280)
        };
        form.Show();
        PumpMessages(200);
        var snapshot = WindowCatalog.EnumerateCandidates().First(window => window.Hwnd == form.Handle);
        var workspace = WorkspaceService.Create(
            "Background window test",
            BuiltInLayouts.Get("halves"),
            ShellSnapBackend.GetPrimaryWorkArea(),
            new[]
            {
                new WorkspaceApplication
                {
                    DisplayName = form.Text,
                    Kind = WorkspaceApplicationKind.Background,
                    BackgroundWindowMode = BackgroundWindowMode.Hide,
                    WindowIdentity = WorkspaceService.IdentityFrom(snapshot),
                    Launch = new ApplicationLaunchSpec { ExecutablePath = executable }
                }
            });
        var stopwatch = Stopwatch.StartNew();
        var prepared = WorkspaceLauncher.PrepareAsync(workspace, TimeSpan.FromSeconds(1))
            .GetAwaiter().GetResult();
        stopwatch.Stop();
        PumpMessages(120);
        var stillVisible = WindowCatalog.EnumerateCandidates().Any(window => window.Hwnd == form.Handle);
        using var delayedForm = new Form
        {
            Text = "Delayed background visibility test",
            StartPosition = FormStartPosition.Manual,
            ShowInTaskbar = true,
            Bounds = new Rectangle(120, 120, 440, 300)
        };
        delayedForm.Show();
        PumpMessages(450);
        var delayedStillVisible = WindowCatalog.EnumerateCandidates().Any(window => window.Hwnd == delayedForm.Handle);
        WorkspaceLauncher.ReleaseBackgroundGuard(workspace);
        delayedForm.Show();
        delayedForm.Visible = true;
        delayedForm.Activate();
        PumpMessages(180);
        var visibleAfterRelease = WindowCatalog.EnumerateCandidates().Any(window => window.Hwnd == delayedForm.Handle);
        var passed = prepared.FailedBackgroundApplications == 0 &&
                     stopwatch.Elapsed < TimeSpan.FromSeconds(1) &&
                     !stillVisible &&
                     !delayedStillVisible &&
                     visibleAfterRelease;
        Console.WriteLine(passed
            ? $"PASS: 恢复事务内窗口被隐藏，事务释放后用户可立即重新显示；准备耗时 {stopwatch.ElapsedMilliseconds} ms。"
            : $"FAIL: 后台事务边界异常。elapsed={stopwatch.ElapsedMilliseconds} immediate={stillVisible} delayed={delayedStillVisible} afterRelease={visibleAfterRelease}");
        return passed ? 0 : 14;
    }

    private static int LayoutRecognizerTest()
    {
        var workArea = new PhysicalRect(0, 0, 1800, 1200);
        var windows = new[]
        {
            new WindowSnapshot(new nint(1), 1, @"C:\Apps\Left.exe", "Left", "Left",
                new PhysicalRect(0, 0, 900, 1200)),
            new WindowSnapshot(new nint(2), 2, @"C:\Apps\Top.exe", "Top", "Top",
                new PhysicalRect(900, 0, 900, 600)),
            new WindowSnapshot(new nint(3), 3, @"C:\Apps\Bottom.exe", "Bottom", "Bottom",
                new PhysicalRect(900, 600, 900, 600))
        };
        var result = LayoutRecognizer.Recognize(workArea, windows);
        var overlapping = new[]
        {
            windows[0] with { VisibleBounds = new PhysicalRect(0, 0, 1400, 1000) },
            windows[1] with { VisibleBounds = new PhysicalRect(300, 100, 1400, 1000) }
        };
        var overlapFallback = LayoutRecognizer.Recognize(workArea, overlapping);
        var passed = !result.IsCustom &&
                     result.Layout.Zones.Count == 3 &&
                     result.Assignments.Count == 3 &&
                     result.Confidence >= 0.95 &&
                     !overlapFallback.IsCustom &&
                     overlapFallback.Assignments.Count == 2;
        Console.WriteLine(passed
            ? $"PASS: 自动识别“{result.Layout.DisplayName}”，置信度 {result.Confidence:P0}。"
            : $"FAIL: 得到 {result.Layout.Id} / custom={result.IsCustom} / {result.Confidence:P0}。");
        return passed ? 0 : 8;
    }

    private static int BackgroundReuseTest()
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定当前测试程序路径。");
        var workspace = WorkspaceService.Create(
            "Background reuse test",
            BuiltInLayouts.Get("halves"),
            new PhysicalRect(0, 0, 1920, 1080),
            new[]
            {
                new WorkspaceApplication
                {
                    DisplayName = "Current test host",
                    Kind = WorkspaceApplicationKind.Background,
                    Launch = new ApplicationLaunchSpec { ExecutablePath = executable }
                }
            });
        var prepared = WorkspaceLauncher.PrepareAsync(workspace, TimeSpan.FromSeconds(1))
            .GetAwaiter().GetResult();
        var passed = prepared.Assignments.Count == 0 &&
                     prepared.MissingWindows.Count == 0 &&
                     prepared.Outcomes.Single().Status == ApplicationStartStatus.AlreadyAvailable;
        Console.WriteLine(passed
            ? "PASS: 已运行的后台进程被复用，且未生成 Snap 分配。"
            : "FAIL: 后台进程复用策略异常。");
        return passed ? 0 : 9;
    }

    private static int PartialLayoutTest(ShellSnapBackend backend)
    {
        var layout = BuiltInLayouts.Get("quarters");
        var passed = RunLayout(
            backend,
            layout,
            keepVisibleMilliseconds: 1200,
            occupiedZoneIds: ["top-left", "bottom-right"]);
        Console.WriteLine(
            passed
                ? "PASS: 四宫格仅占用两个 Zone，另外两个 Zone 保持桌面可见。"
                : "FAIL: 四宫格部分占用测试失败。");
        return passed ? 0 : 7;
    }

    private static bool RunLayout(
        ShellSnapBackend backend,
        SnapLayoutDefinition layout,
        int keepVisibleMilliseconds,
        IReadOnlyList<string>? occupiedZoneIds = null)
    {
        var errors = layout.Validate();
        if (errors.Count > 0)
        {
            Console.Error.WriteLine(string.Join(Environment.NewLine, errors));
            return false;
        }

        var workArea = ShellSnapBackend.GetPrimaryWorkArea();
        var occupiedZones = occupiedZoneIds is null
            ? layout.Zones
            : occupiedZoneIds.Select(layout.GetZone).ToList();
        var forms = new List<Form>(occupiedZones.Count);
        try
        {
            for (var index = 0; index < occupiedZones.Count; index++)
            {
                var zone = occupiedZones[index];
                var form = new Form
                {
                    Text = $"Route 3 · {layout.DisplayName} · {zone.Id}",
                    StartPosition = FormStartPosition.Manual,
                    ShowInTaskbar = true,
                    Bounds = new Rectangle(
                        workArea.X + 80 + index * 36,
                        workArea.Y + 80 + index * 32,
                        520,
                        360),
                    MinimumSize = new Size(180, 120)
                };
                var label = new Label
                {
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Font = new Font("Segoe UI", 16),
                    Text = $"{layout.DisplayName}\r\n{zone.Id}"
                };
                form.Controls.Add(label);
                form.Show();
                forms.Add(form);
            }

            // Shell 必须先完成顶层窗口注册；过早提交会出现 S_OK 但不移动。
            PumpMessages(650);
            var assignments = forms
                .Select((form, index) => new SnapAssignment(form.Handle, occupiedZones[index].Id))
                .ToArray();
            var result = backend.Snap(layout, workArea, assignments);
            PumpMessages(500);
            result = backend.Verify(result);
            PumpMessages(keepVisibleMilliseconds);
            PrintResult(layout, workArea, result);
            return result.Success;
        }
        finally
        {
            foreach (var form in forms)
            {
                form.Close();
                form.Dispose();
            }

            PumpMessages(80);
        }
    }

    private static int CustomLayoutTest(ShellSnapBackend backend)
    {
        var resizedPartition = new SnapLayoutDefinition
        {
            Id = "custom-58-stack",
            DisplayName = "自定义 58% 主窗 + 42% 双栏",
            Zones =
            [
                new SnapZone { Id = "main", Rect = new NormalizedRect { X = 0, Y = 0, Width = 0.58, Height = 1 } },
                new SnapZone { Id = "side-top", Rect = new NormalizedRect { X = 0.58, Y = 0, Width = 0.42, Height = 0.56 } },
                new SnapZone { Id = "side-bottom", Rect = new NormalizedRect { X = 0.58, Y = 0.56, Width = 0.42, Height = 0.44 } }
            ]
        };
        var desktopGap = new SnapLayoutDefinition
        {
            Id = "custom-desktop-gap",
            DisplayName = "自定义右半工作区 + 左半桌面留白",
            Zones =
            [
                new SnapZone { Id = "right", Rect = new NormalizedRect { X = 0.5, Y = 0, Width = 0.5, Height = 1 } }
            ]
        };
        var allBuiltInsAccepted = BuiltInLayouts.All.All(layout => NativeSnapLayoutCatalog.FindExact(layout) is not null);
        var resizedRejected = NativeSnapLayoutCatalog.FindExact(resizedPartition) is null;
        var gapRejected = NativeSnapLayoutCatalog.FindExact(desktopGap) is null;
        var nearest = NativeSnapLayoutCatalog.FindNearest(resizedPartition, resizedPartition.Zones.Count);
        var backendResult = backend.Snap(
            resizedPartition,
            ShellSnapBackend.GetPrimaryWorkArea(),
            [new SnapAssignment((nint)1, "main")]);
        var backendBlockedBeforeSubmission = !backendResult.Success &&
                                             backendResult.Message.Contains("Snap Group", StringComparison.OrdinalIgnoreCase);
        var passed = allBuiltInsAccepted && resizedRejected && gapRejected && nearest is not null && backendBlockedBeforeSubmission;
        Console.WriteLine(passed
            ? $"PASS: 所有原生模型通过；任意比例和缺口布局均在 SnapWindows 调用前被阻止；最近模型={nearest!.Layout.Id}。"
            : "FAIL: 原生布局模型边界回归失败。");
        return passed ? 0 : 8;
    }

    private static int BackgroundExecutableTest(string executablePath)
    {
        var fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("后台测试程序不存在。", fullPath);
        var displayName = Path.GetFileNameWithoutExtension(fullPath);
        var workspace = WorkspaceService.Create(
            $"后台测试 {displayName}",
            BuiltInLayouts.Get("halves"),
            ShellSnapBackend.GetPrimaryWorkArea(),
            [
                new WorkspaceApplication
                {
                    DisplayName = displayName,
                    Kind = WorkspaceApplicationKind.Background,
                    BackgroundWindowMode = BackgroundWindowMode.Hide,
                    Launch = new ApplicationLaunchSpec
                    {
                        ExecutablePath = fullPath,
                        WorkingDirectory = Path.GetDirectoryName(fullPath) ?? string.Empty
                    }
                }
            ]);
        var preparation = WorkspaceLauncher.PrepareAsync(workspace, TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        PumpMessages(2500);
        var processName = Path.GetFileNameWithoutExtension(fullPath);
        var currentSessionId = Process.GetCurrentProcess().SessionId;
        var visibleProcesses = Process.GetProcessesByName(processName)
            .Where(process =>
            {
                using (process)
                {
                    try
                    {
                        if (process.SessionId != currentSessionId) return false;
                        process.Refresh();
                        return process.MainWindowHandle != nint.Zero;
                    }
                    catch { return false; }
                }
            })
            .Count();
        var visibleCatalogWindows = WindowCatalog.EnumerateCandidates().Count(window =>
            string.Equals(Path.GetFileNameWithoutExtension(window.ProcessPath), processName, StringComparison.OrdinalIgnoreCase) ||
            window.Title.Contains(displayName, StringComparison.OrdinalIgnoreCase));
        var outcome = preparation.Outcomes.Single();
        var passed = outcome.Status is ApplicationStartStatus.Started or ApplicationStartStatus.AlreadyAvailable &&
                     visibleProcesses == 0 && visibleCatalogWindows == 0;
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")}: {outcome.Message} " +
                          $"visibleMain={visibleProcesses}, visibleCatalog={visibleCatalogWindows}");
        return passed ? 0 : 11;
    }

    private static int BackgroundAumidTest(string appUserModelId)
    {
        var launch = ShellAppResolver.Enrich(new ApplicationLaunchSpec
        {
            AppUserModelId = appUserModelId
        });
        if (string.IsNullOrWhiteSpace(launch.ExecutablePath) || !File.Exists(launch.ExecutablePath))
        {
            Console.WriteLine($"FAIL: AUMID {appUserModelId} 未解析到桌面程序 EXE。");
            return 16;
        }

        var workspace = WorkspaceService.Create(
            $"AUMID 后台测试 {appUserModelId}",
            BuiltInLayouts.Get("halves"),
            ShellSnapBackend.GetPrimaryWorkArea(),
            [new WorkspaceApplication
            {
                DisplayName = appUserModelId,
                Kind = WorkspaceApplicationKind.Background,
                BackgroundWindowMode = BackgroundWindowMode.Hide,
                Launch = new ApplicationLaunchSpec { AppUserModelId = appUserModelId }
            }]);
        var preparation = WorkspaceLauncher.PrepareAsync(workspace, TimeSpan.FromSeconds(4))
            .GetAwaiter().GetResult();
        PumpMessages(1800);
        var visibleWindows = WindowCatalog.EnumerateCandidates().Count(window =>
            string.Equals(window.ProcessPath, launch.ExecutablePath, StringComparison.OrdinalIgnoreCase));
        var outcome = preparation.Outcomes.Single();
        var passed = preparation.FailedBackgroundApplications == 0 && visibleWindows == 0;
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")}: AUMID={appUserModelId} exe={launch.ExecutablePath} " +
                          $"status={outcome.Status} visible={visibleWindows} message={outcome.Message}");
        return passed ? 0 : 16;
    }

    private static int BackgroundDescendantTest()
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定测试程序路径。");
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var title = $"SnapWorkspace descendant {Guid.NewGuid():N}";
        var escapedExecutable = executable.Replace("'", "''", StringComparison.Ordinal);
        var escapedTitle = title.Replace("'", "''", StringComparison.Ordinal);
        var arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"& '{escapedExecutable}' background-child-window-host '{escapedTitle}'\"";
        var workspace = WorkspaceService.Create(
            "后台子进程窗口测试",
            BuiltInLayouts.Get("halves"),
            ShellSnapBackend.GetPrimaryWorkArea(),
            [new WorkspaceApplication
            {
                DisplayName = "PowerShell 启动器",
                Kind = WorkspaceApplicationKind.Background,
                BackgroundWindowMode = BackgroundWindowMode.Hide,
                Launch = new ApplicationLaunchSpec
                {
                    ExecutablePath = powershell,
                    Arguments = arguments,
                    WorkingDirectory = Path.GetDirectoryName(executable) ?? string.Empty,
                    AlwaysStartNewInstance = true
                }
            }]);

        var preparation = WorkspaceLauncher.PrepareAsync(workspace, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        PumpMessages(900);
        var visible = WindowCatalog.EnumerateCandidates().Any(window =>
            string.Equals(window.Title, title, StringComparison.Ordinal));
        PumpMessages(1100);
        var outcome = preparation.Outcomes.Single();
        var passed = outcome.Status == ApplicationStartStatus.Started && !visible;
        Console.WriteLine(passed
            ? "PASS: 启动器创建的异名子进程窗口被事件监听识别并隐藏。"
            : $"FAIL: 后台子进程窗口仍可见。status={outcome.Status} visible={visible} message={outcome.Message}");
        return passed ? 0 : 15;
    }

    private static int BackgroundChildWindowHost(string title)
    {
        using var form = new Form
        {
            Text = title,
            StartPosition = FormStartPosition.Manual,
            ShowInTaskbar = true,
            Bounds = new Rectangle(160, 160, 520, 320)
        };
        using var closeTimer = new System.Windows.Forms.Timer { Interval = 1600 };
        closeTimer.Tick += (_, _) => form.Close();
        form.Shown += (_, _) => closeTimer.Start();
        Application.Run(form);
        return 0;
    }

    private static int WorkspaceWindowLifecycleTest()
    {
        using var reusedWindow = new Form { Text = "预先存在窗口", Width = 320, Height = 180 };
        using var startedWindow = new Form { Text = "本次启动窗口", Width = 320, Height = 180 };
        reusedWindow.Show();
        startedWindow.Show();
        PumpMessages(100);
        var requested = WorkspaceWindowLifecycle.RequestClose([startedWindow.Handle]);
        PumpMessages(150);
        var passed = requested == 1 && reusedWindow.Visible && !startedWindow.Visible;
        Console.WriteLine(passed
            ? "PASS: 只向指定的本次启动窗口发送正常关闭请求；预先存在窗口保持可见。"
            : "FAIL: 工作区安全结束窗口边界失败。");
        return passed ? 0 : 12;
    }

    private static int RuntimeRoutingTest()
    {
        var settingsLaunch = WorkspaceService.LaunchFrom(new WindowSnapshot(
            new nint(1),
            1,
            Path.Combine(Environment.SystemDirectory, "ApplicationFrameHost.exe"),
            "ApplicationFrameWindow",
            "设置",
            new PhysicalRect(0, 0, 800, 600)));
        using var snapForm = new Form
        {
            Text = $"Snap eligible {Guid.NewGuid():N}",
            FormBorderStyle = FormBorderStyle.Sizable,
            MaximizeBox = true,
            Width = 420,
            Height = 280
        };
        using var fixedForm = new Form
        {
            Text = $"Compatibility fallback {Guid.NewGuid():N}",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            Width = 420,
            Height = 280
        };
        using var legacyCompatibilityForm = new Form
        {
            Text = $"Legacy invalid compatibility bounds {Guid.NewGuid():N}",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            Width = 540,
            Height = 360
        };
        snapForm.Show();
        fixedForm.Show();
        legacyCompatibilityForm.Show();
        PumpMessages(160);
        var catalog = WindowCatalog.EnumerateCandidates();
        var snapSnapshot = catalog.Single(window => window.Hwnd == snapForm.Handle);
        var fixedSnapshot = catalog.Single(window => window.Hwnd == fixedForm.Handle);
        var legacyCompatibilitySnapshot = catalog.Single(window => window.Hwnd == legacyCompatibilityForm.Handle);
        var workArea = ShellSnapBackend.GetPrimaryWorkArea();
        var legacyCompatibilityApplication = WorkspaceService.CreateCompatibilityApplication(
            legacyCompatibilitySnapshot,
            workArea);
        legacyCompatibilityApplication.CompatibilityBounds = new NormalizedRect
        {
            X = 0,
            Y = 0,
            Width = 1d / workArea.Width,
            Height = 1d / workArea.Height
        };
        var workspace = WorkspaceService.Create(
            "Runtime routing",
            BuiltInLayouts.Get("thirds"),
            workArea,
            [
                WorkspaceService.CreateWindowApplication(snapSnapshot, "left"),
                WorkspaceService.CreateWindowApplication(fixedSnapshot, "center"),
                legacyCompatibilityApplication,
                new WorkspaceApplication
                {
                    DisplayName = "Missing optional window",
                    Kind = WorkspaceApplicationKind.Window,
                    ZoneId = "right",
                    WindowIdentity = new WindowIdentity
                    {
                        ProcessPath = @"Z:\Missing\Optional.exe",
                        ClassName = "MissingWindow",
                        ExactTitle = "Missing optional window"
                    },
                    Launch = new ApplicationLaunchSpec()
                }
            ]);
        var prepared = WorkspaceLauncher.PrepareAsync(workspace, TimeSpan.FromMilliseconds(300))
            .GetAwaiter().GetResult();
        var fallback = prepared.CompatibilityAssignments.SingleOrDefault(assignment =>
            assignment.Hwnd == fixedForm.Handle);
        var repairedLegacy = prepared.CompatibilityAssignments.SingleOrDefault(assignment =>
            assignment.Hwnd == legacyCompatibilityForm.Handle);
        var repairedBounds = repairedLegacy?.Bounds.ToPhysical(workArea);
        var passed = string.Equals(settingsLaunch.LaunchUri, "ms-settings:", StringComparison.OrdinalIgnoreCase) &&
                     prepared.Assignments.Count == 1 &&
                     prepared.Assignments[0].Hwnd == snapForm.Handle &&
                      fallback?.Hwnd == fixedForm.Handle &&
                      fallback?.SourceZoneId == "center" &&
                      repairedBounds is { Width: >= 160, Height: >= 100 } &&
                      prepared.MissingWindows.Count == 1;
        Console.WriteLine(passed
            ? "PASS: Windows 设置使用 URI；不可 Snap 窗口自动兼容回退；旧版 1×1 兼容区域自愈；缺失窗口不丢弃已就绪分配。"
            : $"FAIL: runtime routing 异常。uri={settingsLaunch.LaunchUri} native={prepared.Assignments.Count} compatibility={prepared.CompatibilityAssignments.Count} missing={prepared.MissingWindows.Count}");
        return passed ? 0 : 17;
    }

    private static void PrintResult(
        SnapLayoutDefinition layout,
        PhysicalRect workArea,
        SnapOperationResult result)
    {
        Console.WriteLine($"Layout={layout.Id} Zones={layout.Zones.Count} WorkArea={workArea}");
        Console.WriteLine(
            $"Success={result.Success} FallbackRecommended={result.FallbackRecommended} " +
            $"HResult=0x{unchecked((uint)result.HResult):X8}");
        Console.WriteLine(result.Message);
        foreach (var window in result.Windows)
        {
            Console.WriteLine(
                $"  {window.ZoneId,-14} expected={window.ExpectedBounds} " +
                $"actual={window.ActualVisibleBounds?.ToString() ?? "n/a"} " +
                $"verified={window.GeometryVerified}" +
                (window.Error is null ? string.Empty : $" error={window.Error}"));
        }
    }

    private static int ExportLayout(string layoutId, string outputPath)
    {
        var path = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        LayoutJson.Save(path, BuiltInLayouts.Get(layoutId));
        Console.WriteLine(path);
        return 0;
    }

    private static void PumpMessages(int milliseconds)
    {
        var stopAt = Environment.TickCount64 + milliseconds;
        do
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
        while (Environment.TickCount64 < stopAt);
    }

    private static bool IsWindowAbove(nint first, nint second)
    {
        for (var current = first; current != nint.Zero; current = GetWindow(current, 2))
        {
            if (current == second) return true;
        }
        return false;
    }

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hwnd, uint command);

    private static string RequireArgument(string[] args, int index, string name) =>
        args.ElementAtOrDefault(index)
        ?? throw new ArgumentException($"缺少参数：{name}。");

    private static int ShowHelp()
    {
        Console.WriteLine(
            """
            SnapWorkspace.Route3.Demo

              probe
                  只做私有 Shell 能力探测，不移动窗口。

              list-layouts
                  列出全部内置 Snap Layout。

              inspect-windows
                  只读枚举可保存的顶层窗口及其识别字段。

              application-catalog-test
                  验证窗口和后台进程清单去重及系统组件过滤。

              capture-current <layout-id> <workspace.json>
                  按当前窗口与 Zone 的覆盖关系识别并保存工作区。

              restore <workspace.json>
                  匹配当前已有窗口，并一次提交完整 SnapWindows 布局。

              restore-with-launch <workspace.json>
                  启动缺失应用，执行完整后台、原生 Snap、兼容定位和层级稳定事务。

              demo <layout-id>
                  创建隔离测试窗，执行一种布局后自动关闭。

              demo-file <layout.json>
                  加载任意归一化 Zone JSON 并执行。

              self-test
                  依次验证全部内置布局。

              workspace-model-test
                  使用合成窗口验证 schema v5、旧模型迁移、后台应用、JSON 往返和重匹配。

              window-matching-test
                  验证路径、窗口类、通配符和正则标题匹配规则。

              preflight-test
                  验证恢复前检查能识别歧义候选和失效启动路径。

              compatibility-placement-test
                  验证可见窗口不进入 SnapWindows，只执行一次兼容定位。

              background-window-test
                  验证后台应用已有窗口及恢复后延迟出现的窗口均被强制隐藏。

              layout-recognizer-test
                  验证一侧单栏、一侧上下双栏能够被自动识别。

              background-reuse-test
                  验证已运行后台进程视为成功且不占用 Snap Zone。

              background-executable-test <exe>
                  启动指定后台程序并验证当前会话没有留下可见窗口。

              background-aumid-test <aumid>
                  将 AppsFolder AUMID 解析为真实 EXE，并验证严格后台隐藏。

              workspace-window-lifecycle-test
                  验证结束工作区只正常关闭明确标记为本次启动的窗口。

              runtime-routing-test
                  验证系统 URI、非 Snap 窗口兼容回退和可见窗口部分恢复。

              partial-layout-test
                  四宫格只提交两个窗口，验证其余 Zone 可以保持桌面可见。

              custom-layout-test
                  验证任意几何和删除 Zone 的缺口布局会在 SnapWindows 调用前被阻止。

              export-layout <layout-id> <output.json>
                  导出内置布局，作为自定义布局模板。
            """);
        return 0;
    }
}
