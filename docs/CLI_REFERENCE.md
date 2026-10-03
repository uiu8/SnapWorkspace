# 命令行与测试命令

`SnapWorkspace.Route3.Demo` 是开发和诊断宿主，不是普通用户必须使用的第二个应用。它提供只读探测、布局/窗口检查、工作区 JSON 操作和回归测试。

> 带窗口、恢复、后台启动或 `demo` 的命令会改变当前桌面或启动应用。只读命令已在表中标明。

## 基本用法

从仓库根目录执行：

```powershell
dotnet run --project SnapWorkspace.Route3.Demo -c Release -- <command> [arguments]
```

已先构建时可加 `--no-build`：

```powershell
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- probe
```

## 检查环境和清单

### `probe`

只读探测私有 Shell 能力，不移动窗口。

```powershell
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- probe
```

输出 JSON 包含：是否支持、`KnownBaseline`/`RuntimeCompatible`/`Unavailable` 兼容等级、Shell 是否为已知二进制、管理器接口、轴向策略、有效窗口上限、DLL 路径/版本/哈希和 WindowId 提供器。未知哈希但运行时探测通过时退出码仍为 0；不支持时为 2。

### `list-layouts`

只读列出 10 个内置布局及 Zone。

```powershell
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- list-layouts
```

### `inspect-windows`

只读枚举当前可保存顶层窗口及识别字段。输出可能包含敏感路径和标题，不要直接粘贴到公开 issue。

```powershell
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- inspect-windows
```

## 捕捉、恢复和布局样例

### `capture-current <layout-id> <workspace.json>`

按当前窗口与指定 Zone 的覆盖关系建立工作区 JSON。

```powershell
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- `
  capture-current quarters '.\artifacts\captured-workspace.json'
```

### `restore <workspace.json>`

只匹配已经存在的窗口，并提交原生布局；不负责完整缺失应用启动编排。

```powershell
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- `
  restore '.\artifacts\captured-workspace.json'
```

### `restore-with-launch <workspace.json>`

执行完整后台、可见应用启动、原生 Snap、兼容定位和层级稳定事务。

```powershell
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- `
  restore-with-launch '.\artifacts\captured-workspace.json'
```

### `demo <layout-id>`

创建隔离测试窗，运行一个内置布局后自动关闭。

```powershell
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- demo half-left-stack
```

### `demo-file <layout.json>`

加载一个归一化布局文件并尝试执行。任意几何仍会受到原生模型门约束。

```powershell
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- `
  demo-file '.\artifacts\layout.json'
```

### `export-layout <layout-id> <output.json>`

导出内置布局，可作为格式样例。

```powershell
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- `
  export-layout main-right-stack '.\artifacts\main-right-stack.json'
```

## 模型与匹配回归

| 命令 | 作用 | 是否操作真实窗口 |
|---|---|---:|
| `workspace-model-test` | schema v5、旧格式迁移、JSON 往返、角色和重匹配 | 否 |
| `window-matching-test` | 路径、类名、通配符、正则标题匹配 | 否 |
| `preflight-test` | 候选歧义、失效启动路径和预检级别 | 否 |
| `layout-recognizer-test` | 一侧单栏、一侧上下双栏等模型识别 | 否 |
| `application-catalog-test` | 窗口/后台清单去重和系统组件过滤 | 只读枚举 |
| `runtime-routing-test` | 系统 URI、非 Snap 回退和部分恢复路由 | 使用合成/隔离状态 |

运行示例：

```powershell
$tests = @(
  'workspace-model-test',
  'window-matching-test',
  'preflight-test',
  'layout-recognizer-test',
  'application-catalog-test',
  'runtime-routing-test'
)

foreach ($test in $tests) {
  dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- $test
  if ($LASTEXITCODE -ne 0) { throw "$test failed" }
}
```

## 原生与兼容布局回归

| 命令 | 验证内容 | 注意 |
|---|---|---|
| `compatibility-placement-test` | 可见窗口不进入 Snap，只定位一次 | 创建测试窗口 |
| `partial-layout-test` | 四宫格仅提交两个窗口，另外 Zone 空置 | 需要交互桌面和 Route 3 |
| `custom-layout-test` | 任意比例和删除 Zone 在 Shell 调用前被阻止 | 创建测试状态 |
| `self-test` | 依次测试全部内置原生布局 | 会移动/关闭隔离测试窗口 |

```powershell
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- compatibility-placement-test
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- partial-layout-test
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- custom-layout-test
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- self-test
```

不要在无人值守的托管 CI 中运行要求真实 Snap 会话的测试。

## 后台与生命周期回归

| 命令 | 验证内容 |
|---|---|
| `background-window-test` | 已有窗口和延迟出现窗口在事务内被隐藏 |
| `background-reuse-test` | 已运行无窗口进程视为可用，不占 Zone |
| `background-descendant-test` | 启动器创建的子进程窗口仍能被跟踪 |
| `background-executable-test <exe>` | 实际启动指定 EXE 并验证无可见窗口 |
| `background-aumid-test <aumid>` | 解析 AppsFolder AUMID 并验证严格后台 |
| `workspace-window-lifecycle-test` | 结束会话只关闭明确由本次启动的窗口 |

真实应用示例：

```powershell
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- `
  background-executable-test 'D:\software\Everything\Everything.exe'
```

这些命令可能启动或关闭目标程序。先保存未完成工作，并确认 EXE 是测试目标。

## GUI 特殊参数

这些参数主要用于启动项和自动化：

| 参数 | 用途 |
|---|---|
| `--startup` | 当前用户登录启动；配合设置可只进入轻量托盘宿主 |
| `--render-preview <png>` | 渲染确定性 UI 预览 |
| `--settings-preview` | 预览时打开设置页 |
| `--accessibility-test-output <txt>` | 输出无障碍回归结果 |
| `--command-palette-preview` | 渲染命令面板预览路径 |

这些是工程入口，普通用户不需要建立手工快捷方式；开机启动由设置页管理。

## 退出码和证据记录

- 0：命令完成或测试通过；
- 非 0：失败；`probe` 在明确不支持时使用 2；
- 未处理异常会打印到标准错误并返回 1。

提交回归证据时记录：

```powershell
$log = '.\artifacts\route3-probe.txt'
dotnet run --project SnapWorkspace.Route3.Demo -c Release --no-build -- probe *>&1 |
  Tee-Object -FilePath $log
```

公开日志前检查 Shell 路径、用户路径、窗口标题和应用身份。
