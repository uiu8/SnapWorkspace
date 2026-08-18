# 工作区数据格式

Snap Workspace 0.9.5 使用 schema v5 JSON 保存工作区。本文说明文件位置、字段、约束、导入导出和迁移行为。

> 推荐通过 GUI 编辑。手工修改前先退出应用并备份 `%LOCALAPPDATA%\SnapWorkspace`。格式错误的单个文件会被跳过，但不会自动删除。

## 本地目录

```text
%LOCALAPPDATA%\SnapWorkspace\
├─ settings.json
├─ workspaces\
│  └─ <workspace-id>.json
├─ backups\
│  └─ <workspace-id>\
│     └─ yyyyMMdd-HHmmss-fff.json
└─ diagnostics\
   └─ snapworkspace-yyyyMMdd.jsonl
```

工作区文件名使用工作区 `id`。每次覆盖保存前会把旧版本复制到 `backups`，每个工作区保留最近 20 份。

## 完整示例

下面示例使用四宫格，只占用两个原生 Zone，另外包含一个兼容窗口和一个后台应用：

```json
{
  "schemaVersion": 5,
  "id": "6f83a64076ce4bfa91d8cdbb84c4a865",
  "name": "写作工作区",
  "layout": {
    "schemaVersion": 1,
    "id": "quarters",
    "displayName": "四宫格",
    "zones": [
      {
        "id": "top-left",
        "rect": { "x": 0, "y": 0, "width": 0.5, "height": 0.5 }
      },
      {
        "id": "top-right",
        "rect": { "x": 0.5, "y": 0, "width": 0.5, "height": 0.5 }
      },
      {
        "id": "bottom-left",
        "rect": { "x": 0, "y": 0.5, "width": 0.5, "height": 0.5 }
      },
      {
        "id": "bottom-right",
        "rect": { "x": 0.5, "y": 0.5, "width": 0.5, "height": 0.5 }
      }
    ]
  },
  "capturedWorkArea": {
    "x": 0,
    "y": 0,
    "width": 2560,
    "height": 1528
  },
  "applications": [
    {
      "id": "editor",
      "displayName": "记事本",
      "kind": "window",
      "windowPlacement": "nativeSnap",
      "backgroundWindowMode": "hide",
      "zoneId": "top-left",
      "windowIdentity": {
        "processPath": "C:\\Windows\\System32\\notepad.exe",
        "className": "Notepad",
        "exactTitle": "草稿 - 记事本"
      },
      "match": {
        "processMode": "exactPath",
        "titleMode": "contains",
        "titlePattern": "记事本",
        "requireSameWindowClass": false
      },
      "launch": {
        "executablePath": "C:\\Windows\\System32\\notepad.exe",
        "arguments": "",
        "workingDirectory": "C:\\Windows\\System32",
        "runAsAdministrator": false,
        "alwaysStartNewInstance": false
      }
    },
    {
      "id": "browser",
      "displayName": "Google Chrome",
      "kind": "window",
      "windowPlacement": "nativeSnap",
      "backgroundWindowMode": "hide",
      "zoneId": "bottom-right",
      "windowIdentity": {
        "processPath": "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe",
        "className": "Chrome_WidgetWin_1",
        "exactTitle": "新标签页 - Google Chrome"
      },
      "match": {
        "processMode": "exactPath",
        "titleMode": "ignore",
        "titlePattern": "",
        "requireSameWindowClass": true
      },
      "launch": {
        "executablePath": "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe",
        "arguments": "",
        "workingDirectory": "C:\\Program Files\\Google\\Chrome\\Application",
        "runAsAdministrator": false,
        "alwaysStartNewInstance": false
      }
    },
    {
      "id": "legacy-tool",
      "displayName": "EasyPub",
      "kind": "window",
      "windowPlacement": "compatibilityPositioned",
      "backgroundWindowMode": "hide",
      "compatibilityBounds": {
        "x": 0.15,
        "y": 0.55,
        "width": 0.35,
        "height": 0.35
      },
      "windowIdentity": {
        "processPath": "D:\\software\\EasyPub\\EasyPub.exe",
        "className": "#32770",
        "exactTitle": "EasyPub v1.50"
      },
      "match": {
        "processMode": "fileName",
        "titleMode": "contains",
        "titlePattern": "EasyPub",
        "requireSameWindowClass": false
      },
      "launch": {
        "executablePath": "D:\\software\\EasyPub\\EasyPub.exe",
        "arguments": "",
        "workingDirectory": "D:\\software\\EasyPub",
        "runAsAdministrator": false,
        "alwaysStartNewInstance": false
      }
    },
    {
      "id": "search-service",
      "displayName": "Everything",
      "kind": "background",
      "windowPlacement": "nativeSnap",
      "backgroundWindowMode": "hide",
      "match": {
        "processMode": "automatic",
        "titleMode": "automatic",
        "titlePattern": "",
        "requireSameWindowClass": false
      },
      "launch": {
        "executablePath": "D:\\software\\Everything\\Everything.exe",
        "arguments": "",
        "workingDirectory": "D:\\software\\Everything",
        "runAsAdministrator": false,
        "alwaysStartNewInstance": false
      }
    }
  ],
  "createdAt": "2026-08-18T09:00:00+00:00",
  "updatedAt": "2026-08-18T09:30:00+00:00"
}
```

`backgroundWindowMode` 和后台项上的 `windowPlacement` 是统一模型中的兼容字段；后台语义只由 `kind: "background"` 决定，读取时后台模式一律规范化为 `hide`。

## 顶层字段

| 字段 | 类型 | 必需 | 说明 |
|---|---|---:|---|
| `schemaVersion` | integer | 是 | 当前写出值为 `5` |
| `id` | string | 是 | 工作区稳定 Id，通常为 32 位无连字符 GUID |
| `name` | string | 是 | 用户显示名称 |
| `layout` | object | 是 | 完整布局定义 |
| `capturedWorkArea` | object | 是 | 捕捉时主显示器工作区物理像素矩形 |
| `applications` | array | 是 | 至少一个窗口或后台应用 |
| `createdAt` | ISO 8601 | 是 | 创建时间 |
| `updatedAt` | ISO 8601 | 是 | 最近保存时间 |
| `windows` | array | 否 | 仅用于读取 schema v2，保存时移除 |

## 布局和 Zone

`layout` 字段：

| 字段 | 类型 | 说明 |
|---|---|---|
| `schemaVersion` | integer | 布局格式版本，当前为 1 |
| `id` | string | 内置布局 Id 或捕捉布局 Id |
| `displayName` | string | UI 名称 |
| `zones` | array | 1–12 个不重叠区域 |

Zone 字段：

| 字段 | 类型 | 说明 |
|---|---|---|
| `id` | string | 在布局内唯一 |
| `rect.x` / `rect.y` | number | 左上角相对工作区的位置，范围 0–1 |
| `rect.width` / `rect.height` | number | 相对宽高，必须大于 0 |

每个 Zone 必须完整位于 `[0,1] × [0,1]`，Zone 之间不能有面积重叠。模型允许 1–12 个 Zone，但原生提交还要求整个定义精确匹配 10 个内置模型之一。

## 应用字段

| 字段 | 类型 | 说明 |
|---|---|---|
| `id` | string | 工作区内唯一应用 Id |
| `displayName` | string | UI 显示名称 |
| `kind` | enum | `window` 或 `background` |
| `windowPlacement` | enum | 窗口项为 `nativeSnap` 或 `compatibilityPositioned` |
| `backgroundWindowMode` | enum | 当前后台项规范化为 `hide` |
| `zoneId` | string | 仅原生窗口需要；必须引用存在的 Zone |
| `compatibilityBounds` | object | 仅兼容窗口需要；归一化矩形 |
| `windowIdentity` | object | 窗口项必需，后台项可选 |
| `match` | object | 窗口匹配规则 |
| `launch` | object | 启动目标和策略 |

约束：

- 同一个 Zone 只能绑定一个原生窗口；
- 兼容窗口不能同时设置 `zoneId`，必须有有效 `compatibilityBounds`；
- 后台应用不能设置 `zoneId`，且必须可启动；
- 窗口应用必须有 `windowIdentity`；
- 所有应用 Id 必须唯一。

## 窗口身份和匹配

`windowIdentity`：

| 字段 | 类型 | 说明 |
|---|---|---|
| `processPath` | string/null | 捕捉时可执行文件完整路径 |
| `appUserModelId` | string/null | UWP/MSIX 或 AppsFolder 身份 |
| `className` | string | Win32 窗口类 |
| `exactTitle` | string | 捕捉时标题，可为空 |

`match.processMode`：`automatic`、`exactPath`、`fileName`、`ignore`。

`match.titleMode`：`automatic`、`exact`、`contains`、`wildcard`、`regularExpression`、`ignore`。

`match` 还包含：

- `titlePattern`：显式标题模式；为空时可回退到捕捉标题；
- `requireSameWindowClass`：是否强制类名相同。

不允许同时忽略进程、标题且不要求窗口类。启用进程匹配时必须有路径或 AUMID；启用标题匹配时必须有模式或捕捉标题。正则必须能在 100 毫秒超时配置下编译。

## 启动定义

| 字段 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `executablePath` | string/null | null | 传统桌面 EXE |
| `appUserModelId` | string/null | null | AppsFolder/UWP/MSIX 身份 |
| `launchUri` | string/null | null | 系统 URI，例如 `ms-settings:` |
| `arguments` | string | `""` | 启动参数 |
| `workingDirectory` | string | `""` | 工作目录；不存在时预检警告 |
| `runAsAdministrator` | boolean | false | 通过 Shell 提权启动 |
| `alwaysStartNewInstance` | boolean | false | 不复用旧窗口，要求匹配新 HWND |

至少 `executablePath`、`appUserModelId`、`launchUri` 之一存在才可启动。后台项目前要求可解析 EXE 或 AUMID；管理员应用可能超出低权限窗口守卫的控制范围。

## 内置布局 Id

| Id | 显示名称 | Zone Id |
|---|---|---|
| `halves` | 左右二分 | `left`, `right` |
| `wide-left` | 左侧 2/3 + 右侧 1/3 | `main`, `side` |
| `wide-right` | 左侧 1/3 + 右侧 2/3 | `side`, `main` |
| `thirds` | 三列等分 | `left`, `center`, `right` |
| `main-left-stack` | 左侧主窗 + 右侧双栏 | `main`, `side-top`, `side-bottom` |
| `main-right-stack` | 右侧主窗 + 左侧双栏 | `side-top`, `side-bottom`, `main` |
| `half-left-stack` | 左半单栏 + 右半双栏 | `main`, `side-top`, `side-bottom` |
| `half-right-stack` | 右半单栏 + 左半双栏 | `side-top`, `side-bottom`, `main` |
| `center-focus` | 中间主窗 + 两侧窄栏 | `left`, `main`, `right` |
| `quarters` | 四宫格 | `top-left`, `top-right`, `bottom-left`, `bottom-right` |

不要只修改内置布局 Id 而保留不同几何。原生模型门按矩形精确比较，不以名称相信输入。

## `.snapworkspace` 备份包

`.snapworkspace` 是 ZIP 文件：

```text
manifest.json
workspaces\<id-1>.json
workspaces\<id-2>.json
...
```

清单示例：

```json
{
  "format": "SnapWorkspaceArchive",
  "version": 1,
  "exportedAt": "2026-08-18T10:00:00+00:00",
  "workspaceCount": 2
}
```

备份包只包含工作区，不包含 `settings.json`、快捷键、屏蔽规则、诊断日志或应用程序文件。

导入 `.json` 时加载一个工作区；导入 `.snapworkspace` 时校验清单格式并加载 `workspaces/` 下全部 JSON。若 Id 已存在，导入项会生成新 Id、名称追加“（导入）”，现有定义保持不变。

## schema 迁移

读取工作区时会原地规范化，下一次保存写为 schema v5：

| 来源 | 迁移行为 |
|---|---|
| schema v2 `windows` | 非空槽位转换为 `applications` 窗口项 |
| schema v2–v4 | 版本更新为 5，补齐 Id、启动和匹配对象 |
| 旧 `captured-*` / “自定义捕捉”布局 | 原生窗口转为兼容定位，并把基础布局改为 `halves` |
| 旧后台策略 | 一律改为严格 `hide` |
| Windows 设置宿主 | 启动目标改为 `ms-settings:`，保留窗口身份用于匹配 |
| 缺失 EXE/AUMID | 从 `windowIdentity` 回填启动身份 |

迁移不代表所有路径在另一台电脑上有效。导入后仍应运行“检查”查看缺失可执行文件、工作目录和候选歧义。

## `settings.json` 概览

设置不是工作区的一部分，主要字段包括：

- `theme`：`Dark`、`Light` 或系统模式对应值；
- `verificationDelayMilliseconds`：原生提交后验证延迟，UI 运行时限制在 250–1500 毫秒；
- `captureExclusions`、`captureRoleRules`；
- `favoriteApplicationKeys`、`recentApplicationKeys`；
- `enableTrayIcon`、`closeToTray`；
- `runAtStartup`、`startMinimizedAtLogin`；
- `globalHotkeyEnabled`、`globalShortcuts`；
- `diagnosticLoggingEnabled`；
- 两个支持包敏感内容开关。

0.8 及更早版本的单个 `globalHotkey` 和 `quickWorkspaceId` 会迁移到快捷工作区 1，新增动作不会被静默启用。
