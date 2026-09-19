<div align="center">
  <img src="docs/logo.png" width="128" alt="PivKeyUBox Logo" />
  <h1>PivKeyUBox</h1>
  <p><strong>直接长在 Windows 桌面层上的轻量收纳工作台 —— 不生硬、不臃肿、不打扰。</strong></p>
  <p>
    <img src="https://img.shields.io/badge/Platform-Windows%2010%2F11-0078D4?logo=windows&logoColor=white" alt="Platform" />
    <a href="#许可证"><img src="https://img.shields.io/badge/License-MIT-3DA639?logo=opensourceinitiative&logoColor=white" alt="License: MIT" /></a>
    <a href="https://github.com/PivKeyU/PivKeyUBox/releases"><img src="https://img.shields.io/badge/Release-v0.1.0-3478f6?logo=github&logoColor=white" alt="Release" /></a>
    <img src="https://img.shields.io/badge/.NET%20Framework-4.7.2-512BD4?logo=dotnet&logoColor=white" alt=".NET Framework 4.7.2" />
    <img src="https://img.shields.io/badge/React-19-61DAFB?logo=react&logoColor=black" alt="React 19" />
    <img src="https://img.shields.io/badge/WebView2-Runtime-0078D4" alt="WebView2" />
    <img src="https://img.shields.io/badge/Runtime-No%20Electron-6E7681" alt="No Electron runtime" />
    <img src="https://img.shields.io/badge/PRs-Welcome-ff69b4" alt="PRs Welcome" />
  </p>
  <p><a href="README.md">简体中文</a> | <a href="README.en.md">English</a></p>
</div>

<div align="center">
  <img src="docs/hero.png" alt="PivKeyUBox 运行在 Windows 桌面上的整体效果" />
</div>

---

## 这是什么

PivKeyUBox（中文名「片刻收纳」）把桌面文件收纳盒直接挂载在 Windows 桌面层（`SHELLDLL_DefView` / `Progman`）上——**不用手动打开窗口，桌面本身就是工作台**。

每个收纳分区、每张桌面便签都是独立的 WPF 原生窗口，界面由 React 19 + TypeScript 渲染在系统自带的 WebView2 中。**不打包 Electron、不携带 Chromium 运行时**，安装包约 8 MB，冷启动只需拉起 WPF + 系统 WebView2。

> 设计原则：**少一个常驻运行时，多一分桌面安静。** 收纳行为只移动文件、不删除文件，且每一次批量操作都可撤销。

---

## 为什么是 PivKeyUBox

| 对比维度 | PivKeyUBox | Electron 系桌面挂件 | 系统自带桌面图标 | 传统整理工具 |
| --- | --- | --- | --- | --- |
| 运行时体积 | 零 Chromium 运行时，复用系统 WebView2 | 自带完整 Chromium，安装包百 MB 量级 | 无额外运行时 | 视实现而定 |
| 内存占用 | **按需原生窗口**：分区本身是原生 WPF 窗口，只有设置窗口用到 WebView2，且关闭后立即裁剪进程工作集 | 常驻 Chromium 多进程，多窗口共用一份引擎但基线更高 | 由 Explorer 统一承载 | 通常全量常驻 |
| 是否打乱桌面图标布局 | 否。owner 优先指向现存的 `SHELLDLL_DefView`，**从不创建 `WorkerW`** | 视挂载方式，部分方案会抢占桌面层 | 是（图标即文件本身） | 否（但也不在桌面层） |
| 文件语义 | 走 Windows Shell：`IFileOperation` 系统原生进度条、可撤销 | 视实现，多数自行实现文件移动 | 直接就是文件系统 | 多数自行实现 |
| 是否只移不删 | 是。只做移动/复制，**永不删除** | 不一致 | 用户手动操作 | 视实现 |
| 同名冲突处理 | 自动追加 ` (1)`、` (2)`，绝不覆盖 | 不一致 | 系统弹窗询问 | 视实现 |
| 批量撤销/重做 | 内置最多 30 批操作历史，设置页（高级）可撤销/重做 | 少见 | 系统回收站 | 视实现 |
| 是否抢焦点 | 其他应用前台时点击分区不抢焦点；唤起时才整组瞬态置顶 | 常见抢焦点问题 | 无 | 视实现 |
| 安装权限 | Inno Setup `PrivilegesRequired=lowest`，**免 UAC** 安装到用户目录 | 视打包方式，常有 UAC 提示 | 无需安装 | 常见需管理员 |

---

## 核心特性

### 1. 桌面层挂载：长在壁纸上，而不是浮在壁纸上

分区窗口的 owner 优先指向 Explorer 现存的桌面图标视图 `SHELLDLL_DefView`，挂载后**回读验证**；若桌面尚未就绪，回退到 `Progman`，并在桌面就绪后自动升级挂载（约 60 秒后放弃升级线程，不做无意义常驻轮询）。

- **从不主动创建 `WorkerW`**：避免与 Explorer 的图标布局恢复流程打架，登录期不会出现桌面图标错位。
- **Explorer 重启自愈**：监听 `TaskbarCreated` 广播，句柄缓存失效后自动重新挂载全部分区并重挂托盘图标（`NotifyIcon` 不会自行恢复）。
- 挂载与 Z-Order 操作统一走 `SWP_NOACTIVATE`，调整层级时不激活窗口、不闪烁。

### 2. 窗口生命周期与防抢焦

托盘图标左键单击（切换全部分区显示/隐藏）进入一次**唤起会话**：整组窗口以「全员瞬态置顶 → 逆序清除」的原子序列浮到普通层级带顶部——**不是持久 `TopMost`**，其他窗口被激活时仍可正常盖过。

离开后由恢复监视器把整组一次性送回桌面层，回落判定按固定顺序综合三类边沿信号：

| 信号 | 含义 |
| --- | --- |
| `own-foreground-leave` | 分区（或自家任何窗口）曾拿到前台后离开——最可靠的主路径 |
| `foreground-changed` | 从未激活成功但前台窗口发生变化——激活失败也是合法状态 |
| `outside-click` | 50ms 高频采样 `GetAsyncKeyState` 鼠标边沿，兜底跨进程点击 |

同时还有三层保护：唤起后 160ms 抑制窗防误触、交互深度计数（拖动/缩放/菜单期间不回落）、交互深度泄漏看门狗（10 秒无自家前台则强制复位）。**其他应用处于前台时点击分区不会抢占系统焦点。**

### 3. 系统材质背板与主题

材质按系统分平台处理：Win10 1809（build 17134）～ 21999 通过 `SetWindowCompositionAttribute` 挂载 `ACCENT_ENABLE_ACRYLICBLURBEHIND`，由 Windows DWM 直接把壁纸模糊垫在面板底面——是系统级材质，不是 CSS 模拟。Win11（build ≥ 22000）起 SWCA 亚克力背板按窗口**矩形**绘制、无视逐像素 alpha 与窗口 region，白色雾色会从圆角外四个角漏出，因此**主动停用**，玻璃感改由 **WPF 渐变层**独立承担。

- 更早的系统（build < 17134）退化为 `ACCENT_ENABLE_BLURBEHIND` 普通模糊；`SetWindowCompositionAttribute` 调用失败时再退回纯色渐变兜底，视觉不塌陷。
- 浅色 / 深色 / 跟随系统三种主题，5 套配色方案（纯白、暖纸、粉蓝、薄荷、樱粉）+ 完全自定义强调色与底色。
- 透明度、界面缩放、紧凑排布、图标尺寸、标签位置、网格/列表视图均可调。

<div align="center">
  <img src="docs/settings-appearance.png" alt="设置 · 外观：配色方案、透明度与布局选项" />
</div>

### 4. 原生 Shell 文件语义与文件保护

- **交互导入（拖拽进分区 / 设置页移动）**：`IFileOperation` 在专用 STA 线程批量执行，带**系统原生进度条**，操作可在资源管理器中撤销。
- **后台整理（静默模式）**：同卷走 `File.Move`（rename 语义，原子且快）；遇到跨卷（`ERROR_NOT_SAME_DEVICE`）自动把该项退回 Shell 静默批量（`FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI`）。
- **只移不删**：手动「整理桌面...」先弹出待移动清单等用户确认；自动收纳仅在用户主动开启后，处理状态已经稳定的已匹配文件。
- **同名防覆盖**：目标已存在时依次尝试「名称 (1)」、「名称 (2)」……批量内部还用保留集合防止同一批任务互相撞名。
- **放回桌面**：分区内右键即可把文件移回桌面（同样走原生桥，同卷 rename）。

### 5. 桌面便签与待办

便签是独立原生窗口，支持两种形态自由切换，标题双击即可原地改名：

- **待办清单模式**：勾选完成、删除单项、一键清空已完成（菜单会显示已完成数量）、底部快速录入条。
- **随手备忘模式**：自由文本记事，编辑内容 400ms 防抖自动落盘。
- **胶囊折叠**：折叠为 30px 长条（显示标题与完成进度），或折叠为 48×48 小图标（待办会显示未完成数量的角标，超过 99 显示 99+）。
- **图钉置顶 / 贴在桌面底层**：置顶走真正的 `Topmost`，取消后重新挂回桌面层。
- **多种创建入口**：系统托盘、分区标题栏右键菜单、便签内「+」按钮、以及可开关的 **Windows 桌面空白处右键菜单**（注册表 `HKCU\Software\Classes\Directory\Background\shell\PivkeyNotes`，关闭该开关时会自动删除该注册表项）。

<div align="center">
  <img src="docs/note.png" alt="桌面待办便签与随手备忘" />
</div>

### 6. 图标胶囊模式

开启后，折叠的分区不再是细长条，而是收敛成 **56×56 的桌面小图标**（便签为 48×48）。点击胶囊展开、拖动胶囊移动位置，两者通过位移阈值区分。折叠/展开带 210ms 高度与宽度补间，展开时会驱动其他分区**就近让位**，避免内容互相叠加。

<div align="center">
  <img src="docs/capsules.png" alt="折叠为图标胶囊的收纳分区" />
</div>

### 7. 快速搜索与桌面右键

- **快速搜索窗**（`Ctrl + Alt + F`）：轻量 WPF 窗口，**不启用第二个 WebView2**，只在按下快捷键时创建；搜索名称、路径或分类，`Enter` 打开、`Esc` 关闭。
- **全局热键**：`Ctrl + Alt + Q` 切换鼠标穿透，`Ctrl + Alt + S` 打开设置，`Ctrl + Alt + F` 打开快速搜索。
- **分区内搜索**：分区标题栏的搜索按钮就地展开过滤框。

### 8. 免 UAC 安装与在线更新

- **Inno Setup `PrivilegesRequired=lowest`**：默认安装到 `%LOCALAPPDATA%\Programs\PivKeyUBox`，不弹 UAC。
- 安装前/卸载前自动 `taskkill` 关闭可能在后台运行的 `PivKeyUBox.exe / PivkeyOrganizer.exe / ShellMenu.exe`。
- 可选注册 **`HKCU\...\CurrentVersion\Run`** 开机自启、可选创建桌面快捷方式，卸载时自动清理。
- **在线更新**：原生 C# 更新引擎直连 `https://api.github.com/repos/PivKeyU/PivKeyUBox/releases/latest`，带语义化版本比对、更新日志展示、异步下载进度回报；下载完成后以 `/SILENT /CLOSEAPPLICATIONS` 平滑覆盖升级，**保留用户全部配置与数据**。

<div align="center">
  <img src="docs/settings-about.png" alt="设置 · 关于与更新：版本信息、在线检查更新" />
</div>

### 9. 更多能力

| 能力 | 说明 |
| --- | --- |
| 魔法取色 | 开启后按分区内**占比最高**的文件类型自动决定分区颜色（胶囊、色块与标题条跟随内容） |
| 多工作区预设 | 内置「办公 / 游戏 / 开发」三套布局，另可把当前位置、尺寸、折叠、固定、视图与排序模式保存为自定义快照 |
| 自定义分类规则 | 支持按扩展名、名称通配符、路径三种方式匹配，规则按优先级自上而下命中 |
| 配置导入导出 | 高级设置中可把分类、规则与布局导出为 JSON，或一键导入覆盖 |
| 自动扫描与自动整理 | 可配置整理延迟（5–60 秒），只处理状态已经稳定下来的已匹配文件；交互期间自动暂停 |
| 鼠标穿透 | `Ctrl + Alt + Q` 全局切换，窗口加上 `WS_EX_TRANSPARENT` 后点击直接穿透到桌面 |
| 收纳历史 | 最多保留 30 批操作，支持撤销与重做 |

---

## 效果预览

| 收纳分区 | 桌面便签 |
| --- | --- |
| ![收纳分区面板](docs/panels.png) | ![桌面待办便签](docs/note.png) |

| 图标胶囊 | 设置 · 外观 | 设置 · 关于与更新 |
| --- | --- | --- |
| ![图标胶囊](docs/capsules.png) | ![设置外观](docs/settings-appearance.png) | ![设置关于](docs/settings-about.png) |

---

## 技术架构

```text
┌──────────────────────────────────────────────────────────────┐
│  Windows 桌面层（Explorer）                                  │
│  SHELLDLL_DefView（首选） → Progman（回退）                  │
│  · 从不创建 WorkerW    · TaskbarCreated 广播后重新挂载       │
└───────────────────────────┬──────────────────────────────────┘
                            │ HWND owner 挂载 + SWP_NOACTIVATE 层级调度
┌───────────────────────────▼──────────────────────────────────┐
│  原生宿主（C# 5 / WPF / .NET Framework 4.7.2）               │
│  · PivkeyHost.cs    宿主、托盘、热键、WebView2 环境          │
│  · Manager.cs       扫描/分类/收纳计划/配置/同步             │
│  · PanelWindow.cs   收纳分区窗口（折叠/胶囊/视图/排序/拖放） │
│  · NoteWindow.cs    桌面便签与待办窗口                       │
│  · WidgetLayer.cs   桌面层挂载 + Z-Order 协议（带回读验证）  │
│  · LayerSession.cs  唤起会话 + 三信号恢复监视器              │
│  · NativeFileOps.cs IFileOperation / File.Move 原生文件桥    │
│  · UpdateManager.cs GitHub Releases 检测与下载               │
│  · ShellMenu.exe    独立进程的原生右键菜单 helper            │
└───────────────────────────┬──────────────────────────────────┘
                            │ WebView2（系统运行时，非打包）
┌───────────────────────────▼──────────────────────────────────┐
│  React 19 + TypeScript 界面层                                │
│  App.tsx · components/ · hooks/ · state/ · utils/            │
│  · 设置面板 / 关于与更新 / 布局预览 / 图标选择器             │
│  · 仅设置窗口使用 WebView2，关闭后立即裁剪工作集             │
└───────────────────────────┬──────────────────────────────────┘
                            │ postMessage JSON RPC（含来源校验）
┌───────────────────────────▼──────────────────────────────────┐
│  原生 RPC 桥（services/nativeDesktop.ts ↔ Manager）          │
│  scanDesktop / executeOrganization / restoreOrganization     │
│  createNote / deleteNote / setInteractiveRegions / ...       │
└──────────────────────────────────────────────────────────────┘
```

| 层 | 技术 | 说明 |
| --- | --- | --- |
| 桌面挂载 | Win32（`SetWindowPos` / `EnumWindows`） | owner 指向 `SHELLDLL_DefView`，回读验证，失败回退 `Progman` |
| 原生宿主 | C# 5 + WPF（`csc /langversion:5 /platform:x64`） | 编译期只依赖 .NET Framework 4.7.2 与 WebView2 SDK |
| 材质 | `SetWindowCompositionAttribute` + `ACCENT_ENABLE_ACRYLICBLURBEHIND` | Win10 1809–21999 走 DWM 真亚克力；Win11 主动停用 SWCA 亚克力（背板按矩形绘制、圆角漏色），玻璃感由 WPF 渐变层承担；更早系统退化 `ACCENT_ENABLE_BLURBEHIND` |
| 文件操作 | `IFileOperation`（STA 专用线程）+ `File.Move` | 交互操作走 Shell（可撤销），静默整理走 rename 优先 |
| 界面渲染 | React 19 + TypeScript + Vite 6 | 产物 `dist/` 被复制进 `release/PivKeyUBox/web` |
| 宿主容器 | Microsoft Edge WebView2（系统运行时） | `--renderer-process-limit=1`，设置窗口与迁移页共用渲染进程 |
| 图标 | `@phosphor-icons/react` + 内嵌 SVG 路径 | 原生菜单与前端共用同一套图标语义 |
| 界面语言 | 系统 UI 字体（Microsoft YaHei UI / Segoe UI Variable Text） | 托管字体资源随包发布，缺失时回退系统字体 |
| 测试 | Vitest | 6 个测试文件：分类器、布局、面板同步、偏好、魔法取色、分类校验 |
| 打包 | PowerShell + `csc.exe` + Inno Setup 6 | 便携目录、ZIP、单文件安装包一次产出 |
| 自动更新 | GitHub Releases API + Inno Setup `/SILENT` | 版本比对、日志展示、进度条、平滑覆盖 |

---

## 快速开始

### 环境要求

| 项目 | 要求 |
| --- | --- |
| 操作系统 | Windows 10 (1809+) 或 Windows 11 |
| 运行时 | .NET Framework 4.7.2+（Win10 1809+ 通常已内置） |
| WebView2 | Microsoft Edge WebView2 Runtime（Windows 11 默认已预装） |
| Node.js（仅源码构建需要） | 建议 Node.js 18+，用于 Vite 与 Vitest |
| Inno Setup（仅打包安装包需要） | Inno Setup 6，可用 `winget install JRSoftware.InnoSetup` 安装 |

### 方式一：下载安装包（推荐）

前往 [Releases 页面](https://github.com/PivKeyU/PivKeyUBox/releases) 下载最新版本：

- `PivKeyUBox-Setup-v0.1.0.exe` —— 单文件向导式安装包，约 8 MB，**免 UAC**，安装到用户目录。
- `PivKeyUBox-win-x64.zip` —— 便携版压缩包，解压后直接运行 `PivKeyUBox.exe`。

安装完成后程序常驻系统托盘，双击托盘图标打开设置，左键单击切换全部分区显示/隐藏。

### 方式二：源码构建

```powershell
# 安装依赖（React 19 + Phosphor Icons + Vite/Vitest）
npm install

# 启动前端开发服务器（http://127.0.0.1:5173）
# 浏览器模式使用内置 Mock 演示数据，不会读写真实桌面
npm run dev

# 构建前端产物并编译原生宿主，随后直接启动桌面端
# 内部流程：tsc -b && vite build → scripts\build-windows.ps1 → start release\PivKeyUBox\PivKeyUBox.exe
npm run desktop

# 只构建便携产物（release\PivKeyUBox\ 目录 + release\PivKeyUBox-win-x64.zip）
npm run package

# 构建 Inno Setup 安装包（release\PivKeyUBox-Setup-v<版本号>.exe）
# 会自动搜索 ISCC.exe：PATH → %LOCALAPPDATA%\Programs\Inno Setup 6 → Program Files
npm run installer

# 运行单元测试（Vitest，6 个测试文件）
npm test
```

> 打包脚本会自动从 NuGet 下载 WebView2 SDK 并做 **SHA256 校验**（`1.0.4078.44`），编译前自动关闭正在运行的 `PivKeyUBox / PivkeyOrganizer / ShellMenu` 进程，避免 `release\` 下的文件被锁。

---

## 使用指南

### 托盘菜单

在任务栏右下角右键 PivKeyUBox 托盘图标：

| 菜单项 | 作用 |
| --- | --- |
| 整理桌面... | 扫描桌面并弹出待移动清单，确认后批量收纳（只移动、不删除） |
| 新建格子 | 打开「新建分区」对话框，自定义名称、扩展名、颜色与是否接收文件夹 |
| 新建待办清单 | 在桌面创建一张待办便签 |
| 新建随手备忘 | 在桌面创建一张自由记事便签 |
| 新建文件夹映射 | 打开系统文件夹选择器，用所选文件夹名（重名追加 ` (1)`、` (2)`）快速新建一个接收所有扩展名与文件夹的分区 |
| 添加功能格子 | 与「新建格子」完全相同，同样打开「新建分区」对话框 |
| 打开收纳目录 | 用资源管理器打开 `桌面\片刻收纳` |
| 设置 | 打开设置窗口（`Ctrl + Alt + S` 亦可） |
| 退出 | 结束程序（托盘常驻应用，关闭窗口不会退出进程） |

**托盘交互**：左键单击切换全部分区显示/隐藏；双击打开设置。

**门户分区**：真正「只引用不移动」的分区请到 **设置 → 布局 → 分类 → 文件夹门户** 选择文件夹；门户分区始终只引用不移动，收纳不会移动其中的文件。

### 分区交互

- **标题栏**：拖动移动位置，双击折叠/展开，右键打开完整菜单。
- **右键菜单**：重命名分区、固定到桌面/取消固定、折叠/展开、新建待办清单、新建随手备忘、打开文件夹、刷新分区、以及「视图与排序」子菜单（网格视图 / 列表视图 / 按首字母·时间·大小排序）。
- **缩放**：八方向边框缩放，右下角有缩放手柄；折叠或胶囊状态下自动隐藏。
- **文件拖放**：把文件从资源管理器拖进分区即可收纳，悬停时边框会亮起主题色光晕；按住 `Ctrl` 拖放为复制、右键拖放会弹出「移动到 / 复制到」选择菜单。
- **文件操作**：打开、打开原位置、放回桌面、批量收纳到本分区、调用 Windows 原生右键菜单（由独立的 `ShellMenu.exe` 承载，崩溃不会波及主程序）。
- **自动隐藏**：可配置离开后 1–10 秒自动收起；固定（图钉）或折叠状态下不触发。

### 便签与待办

| 操作 | 方式 |
| --- | --- |
| 新建 | 托盘菜单、分区标题栏右键、便签内「+」、Windows 桌面空白处右键 |
| 切换模式 | 标题栏「⇄」按钮，或右键菜单「切换为待办清单 / 切换为随手备忘」 |
| 重命名 | 双击标题，或右键菜单「重命名便签」 |
| 置顶 | 标题栏图钉按钮，或右键菜单「固定到最顶层 / 取消固定」 |
| 折叠 | 标题栏折叠按钮；折叠形态取决于设置中的「便签收纳模式」（胶囊条 / 小图标） |
| 清空已完成 | 待办模式下右键菜单「清空已完成项目 (n)」 |
| 删除 | 右键菜单「删除此便签」（带二次确认弹窗） |

---

## 项目结构

```text
PivKeyUBox/
├── native/                            # Windows 原生宿主（C# 5 / WPF / .NET Framework 4.7.2）
│   ├── PivkeyHost.cs                  # 宿主窗口、托盘菜单、全局热键、WebView2 环境、单实例唤醒
│   ├── Manager.cs                     # 扫描 / 分类规则 / 收纳计划 / 配置持久化 / 面板与便签同步
│   ├── PanelWindow.cs                 # 收纳分区原生窗口：折叠、胶囊、视图排序、拖放、缩放
│   ├── NoteWindow.cs                  # 桌面便签与待办清单原生窗口
│   ├── WidgetLayer.cs                 # 桌面层挂载与 Z-Order 协议（带回读验证与回退升级）
│   ├── LayerSession.cs                # 唤起会话与三信号恢复监视器
│   ├── NativeFileOps.cs               # IFileOperation / File.Move 原生文件桥
│   ├── UpdateManager.cs               # GitHub Releases 版本检测与安装包下载
│   ├── ShellMenu.cs                   # 独立进程的原生右键菜单 helper（ShellMenu.exe）
│   ├── ShellLauncher.cs               # 借 Explorer 宿主启动文件的 Shell 语义启动器
│   ├── CategoryDialog.cs              # 「新建分区」对话框
│   ├── ConfirmDialog.cs               # 现代化二次确认弹窗
│   ├── QuickSearchWindow.cs           # 快速搜索窗（Ctrl + Alt + F）
│   ├── SettingsWindow.cs              # 设置窗口（WebView2 容器 + JSON RPC 路由）
│   ├── FontResources.cs               # 界面字体解析与回退
│   ├── PivkeyOrganizer.exe.config      # 运行时版本声明（.NET Framework 4.7.2）
│   └── PivkeyOrganizer.manifest        # asInvoker + PerMonitorV2 + longPathAware
├── src/                               # React 19 + TypeScript 界面层
│   ├── App.tsx                        # 应用根组件：状态编排、原生桥接、模式路由
│   ├── main.tsx                       # 入口挂载
│   ├── styles.css                     # 全部界面样式（Fluent 卡片语言）
│   ├── types.ts                       # 前后端共享类型契约
│   ├── components/                    # 设置面板、高级设置、布局预览、图标选择器、模态框、Toast
│   ├── hooks/                         # 桌面扫描、原生 Shell 桥接、面板同步、交互区域、持久化状态
│   ├── services/                      # 原生 RPC 桥、文件分类器、分区校验
│   ├── state/                         # 偏好持久化、布局模式、主题令牌、魔法取色、存储键
│   ├── utils/                         # 面板布局算法（自适应预设/对齐）与面板同步负载
│   ├── data/                          # 默认分区定义与角色图标映射
│   └── assets/                        # 字体、图标、原创手绘角色形象
├── scripts/                           # 构建与打包自动化
│   ├── build-windows.ps1              # 拉取并校验 WebView2 SDK，编译宿主与便携包
│   ├── build-installer.ps1            # 定位 ISCC.exe，产出 Inno Setup 安装包与 SHA256
│   └── installer.iss                  # Inno Setup 6 打包配置（免 UAC、开机自启、进程清理）
├── docs/                              # README 配图
├── index.html                         # WebView2 页面入口（含严格 CSP）
├── vite.config.ts                     # Vite 配置（React 插件、固定端口 5173）
├── package.json                       # 脚本与依赖
└── release/                           # 构建输出目录（已 gitignore）
```

---

## 安全与可靠性

- **只移不删**：所有收纳行为都是移动或复制。手动整理会先展示待移动清单（超过 8 项折叠为「……另有 N 个项目」）并等待确认。
- **同名防覆盖**：目标路径已存在时自动追加 ` (1)`、` (2)` 后缀；批量内使用保留集合，避免同一批次内部互相覆盖。
- **配置原子写入 + 快照自愈**：先写临时文件再 `File.Replace` 原子替换，替换时把上一份可用配置转存为 `config.json.bak`。主配置损坏时自动从快照恢复并提示用户；快照也不可用时才重置为默认设置。
- **配置防抖**：变更后 180ms 防抖写盘，避免高频拖动产生大量磁盘写入；尺寸变更单独使用 160ms 防抖重排。
- **更新覆盖保留用户数据**：安装器安装到用户专属空间，升级与卸载都不会触碰用户已设置的分类规则、便签与收纳数据。
- **免 UAC 用户级安装**：`PrivilegesRequired=lowest`，全程无管理员提权，也不写入需要提权的注册表位置。
- **页面安全边界**：WebView2 内禁用 DevTools、默认右键菜单、状态栏、缩放控件与宿主对象；导航前校验来源，非受信地址直接取消；`index.html` 内置严格 CSP（`connect-src 'none'`，Web 界面层零网络需求，网络只用于原生侧的更新检查）。
- **高 DPI 与长路径**：清单声明 `PerMonitorV2,PerMonitor` 与 `longPathAware`；窗口在 DPI 变化发生在拖拽/缩放手势中时，会等手势结束再恢复物理尺寸。

---

## 打包与发布流程

1. **修改版本号**：编辑 `package.json` 中的 `version` 字段，例如改为 `0.2.0`（安装包文件名由脚本自动带上该版本号）。
2. **构建安装包**：

   ```powershell
   npm run installer
   # 产出 release\PivKeyUBox-Setup-v0.2.0.exe，并在控制台打印文件大小与 SHA256
   ```

3. **创建 GitHub Release**：
   - 打开 [Releases 页面](https://github.com/PivKeyU/PivKeyUBox/releases)，点击 **Draft a new release**。
   - Tag 填写 `v0.2.0`（建议带 `v` 前缀，客户端会自动剥掉前缀再比对）。
   - 填写更新日志（Markdown，客户端会原样展示在「关于与更新」里）。
   - 把 `release\PivKeyUBox-Setup-v0.2.0.exe` 作为 Release 附件上传。
   - 点击 **Publish release**。

4. **客户端检查更新**：用户打开设置 → **关于与更新** → **检查更新**，程序请求 `releases/latest` 接口，展示最新版本号与更新日志；点「下载更新安装包」带进度条下载，完成后点「立即重启并安装更新」以 `/SILENT /CLOSEAPPLICATIONS` 平滑覆盖。

---

## 常见问题

<details>
<summary>启动后桌面上看不到任何分区？</summary>

分区默认排布在屏幕左上角附近。可以这样排查：

1. 左键单击托盘图标切换全部分区显示/隐藏，确认不是被隐藏了；
2. 打开设置 → 布局 → 屏幕自适应预设，选择「智能平衡」或「右侧停靠」重新计算位置与尺寸；
3. 若刚从多屏切换到单屏，越界的分区会被自动收回可视区，可再手动拖动一次；
4. Explorer 刚刚重启时，程序会在收到 `TaskbarCreated` 广播后自动重新挂载，通常一两秒内恢复。

</details>

<details>
<summary>会不会把我的桌面图标搞乱？</summary>

不会。程序**从不创建 `WorkerW`**，挂载 owner 优先指向 Explorer 已经存在的 `SHELLDLL_DefView`（带回读验证），只在自己的窗口上调整层级，不重排系统图标。所有层级调整都带 `SWP_NOACTIVATE`，不激活、不闪烁。

</details>

<details>
<summary>误收纳了，怎么恢复？</summary>

两种方式：

- **单个文件**：在分区里右键该文件 → 「放回桌面」。
- **整批操作**：设置页（高级）提供「撤销上次收纳 / 重做」，最多保留 30 批操作历史。若整批放回时个别文件因占用失败，会单独提示失败清单。

</details>

<details>
<summary>需要自己安装 WebView2 运行时吗？</summary>

Windows 11 默认已预装，Windows 10 (1809+) 多数通过 Edge 更新也已具备。若启动时提示 WebView2 环境创建失败，到微软官网安装「Microsoft Edge WebView2 Runtime」即可。程序复用系统运行时，**不会把自己的 Chromium 打进安装包**。

</details>

<details>
<summary>需要管理员权限吗？</summary>

不需要。安装包采用 `PrivilegesRequired=lowest`，默认安装到 `%LOCALAPPDATA%\Programs\PivKeyUBox`，开机自启写的是当前用户 `HKCU` 的 Run 项。可执行文件清单声明的是 `asInvoker`。

</details>

<details>
<summary>为什么不用 Electron？</summary>

因为这是一个常驻桌面层、但大多数时间应该「不存在感」的工具。Electron 会带来一份完整的 Chromium 运行时和更高的内存基线；本项目的界面只在需要时渲染（设置窗口、展开的分区），宿主用 .NET Framework + WPF，容器复用系统 WebView2。安装包因此只有约 8 MB，也不需要为运行时单独做升级通道。

</details>

<details>
<summary>高 DPI 与多显示器支持如何？</summary>

清单声明 `PerMonitorV2,PerMonitor`，每个窗口按自身显示器 DPI 换算坐标；若 DPI 变化发生在拖拽或缩放手势中，会等手势结束后再恢复物理尺寸，避免窗口在移动过程中跳变。多显示器下若分区越界，会被自动收回到可视工作区。

</details>

---

## 路线图

- [x] 桌面层挂载（`SHELLDLL_DefView` 优先 + `Progman` 回退 + Explorer 重启自愈）
- [x] 系统材质背板（Win10 走 DWM 真亚克力，Win11 由 WPF 渐变层承担玻璃感）与浅色/深色/跟随系统主题
- [x] 原生 Shell 文件操作（`IFileOperation` + 跨卷回退 + 同名防覆盖）
- [x] 桌面便签与待办清单（双模式、胶囊折叠、图钉置顶）
- [x] 图标胶囊模式
- [x] Inno Setup 免 UAC 安装包与开机自启
- [x] GitHub Releases 在线检测与平滑覆盖升级
- [x] 快速搜索窗与全局热键
- [x] 多工作区预设（办公 / 游戏 / 开发 + 自定义快照）
- [x] 收纳撤销/重做历史
- [ ] 更细粒度的分类规则编辑器（正则匹配与规则导入导出）
- [ ] 便签与收纳数据的一键云备份/恢复
- [ ] 界面多语言（当前界面文案为简体中文）

---

## 贡献指南

欢迎提交 Issue 与 Pull Request。

1. **Fork** 本仓库并创建特性分支：`git checkout -b feat/your-feature`（修复用 `fix/`，文档用 `docs/`）。
2. **本地开发**：`npm install` 后用 `npm run dev` 调前端界面，用 `npm run desktop` 验证真实桌面行为。
3. **提交规范**：使用 [Conventional Commits](https://www.conventionalcommits.org/) 风格，例如 `feat: 支持正则分类规则`、`fix: 修复 Explorer 重启后胶囊位置丢失`。
4. **跑测试**：提交前执行 `npm test`，并确认 `npm run build` 无 TypeScript 报错。
5. **原生代码约定**：`native/` 下保持 C# 5 语法（`csc /langversion:5`）、中文注释、UTF-8 无 BOM。
6. **提 PR**：说明改动动机、验证方式与影响范围，附上必要的截图或录屏。

---

## 致谢

- **[Maple Mono NF CN](https://github.com/subframe7536/maple-font)** —— 仓库内置该等宽字体资源（`src/assets/fonts/`），原生宿主的字体解析器（`native/FontResources.cs`）会优先查找随包字体文件，缺失时回退到系统 UI 字体。
- **[Phosphor Icons](https://phosphoricons.com/)** —— 原生窗口与前端界面共用的图标体系（`@phosphor-icons/react` 与内嵌 SVG 路径）。
- **原创手绘角色形象** —— 分区图标、便签图示与设置页中的角色插画均为本项目原创素材。

---

## 许可证

本项目基于 **MIT License** 发布。完整条款见仓库根目录的 [`LICENSE`](LICENSE)。

---

<div align="center">
  <sub>如果这个项目对你有帮助，欢迎点一个 ⭐ Star</sub>
</div>
