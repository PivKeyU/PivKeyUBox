# PivKeyUBox / 桌面智能挂载与收纳工作台

PivKeyUBox 是一个直接挂载在 Windows 桌面层上的轻量级桌面文件整理与效率工作台。每个收纳分区和桌面便签均为独立 WPF 原生窗口，界面采用 React 19 + TypeScript + WebView2 渲染，不携带任何 Electron/Chromium 臃肿运行时，兼具原生级别的性能与现代 Fluent/Liquid Glass 界面质感。

```text
Windows 桌面层挂载 (HWND 挂载 SHELLDLL_DefView / Progman)
  -> 窗口层级生命周期 (DeskBox 规范: 全员瞬态唤起 / 离开平滑送回桌面层)
  -> 材质渲染: DWM SetWindowCompositionAttribute 真实亚克力 (Win10/11 优雅降级)
  -> 原生 Shell 桥: IFileOperation (STA 批量执行) / ShellExecute / 变更对账
  -> 在线自动更新: GitHub Releases API + Inno Setup 静默覆盖升级
```

---

## 核心特性

- **直接挂载 Windows 桌面层**：
  每个分区以独立 HWND 挂载到 Explorer 桌面层，owner 优先指向现存的 `SHELLDLL_DefView`（桌面图标视图），带回读验证与故障自动降级（未就绪时回退 `Progman` 并自动检测升级）。从不主动创建 `WorkerW`，彻底避免系统登录期打乱桌面图标布局。Explorer 重启（`TaskbarCreated` 广播）后自动重建挂载并恢复托盘图标。
- **智能窗口生命周期与防抢焦**：
  托盘“显示全部分区”进入唤起会话——整组窗口以“全员瞬态置顶 → 逆序清除”浮动至普通窗口顶部（非持久 TopMost，其他窗口激活时正常覆盖）；离开后由恢复监视器综合多项边沿信号一次性将整组送回桌面层。其他前台应用处于活动状态时点击分区不会抢占系统焦点。
- **真亚克力材质与 Fluent 视觉**：
  通过 `SetWindowCompositionAttribute` 挂载 `ACCENT_ENABLE_ACRYLICBLURBEHIND`，由 Windows DWM 直接把壁纸模糊垫在面板底面。支持浅色/深色主题、强调色自定义、透明度微调与紧凑排布。
- **原生 Shell 语义与文件保护**：
  文件操作直接走 Windows Shell 桥（`IFileOperation` 在专用 STA 线程批量执行，带系统原生进度可撤销）；后台整理同卷执行 `File.Move`（rename 语义），跨卷自动退回 Shell 静默批量。文件只移动不删除，同名目标自动追加序号保护。
- **独立桌面便签与待办备忘**：
  支持随时从桌面空白处右键或系统托盘创建独立便签与待办清单，支持胶囊模式折叠收起、置顶悬浮与双向切换。
- **一键 Inno Setup 纯净安装包**：
  支持打包为向导式安装程序，默认安装到用户目录（`%LOCALAPPDATA%\Programs\PivKeyUBox`），**免 UAC 管理员提权**。可选注册开机自启（注册表 Run 项）、创建桌面快捷方式，安装与升级时自动检测并关闭运行中进程。
- **在线检查更新与应用内平滑升级**：
  原生 C# 更新引擎直接对接 GitHub Releases API，支持语义化版本比对、更新说明（Changelog）展示、异步多线程下载进度条汇报，以及一键平滑重启覆盖安装。

---

## 快速上手与运行

### 1. 运行环境要求
- **系统**：Windows 10 (1809+) 或 Windows 11
- **运行时**：.NET Framework 4.7.2+ 与 Microsoft Edge WebView2 Runtime（Windows 11 默认已预装）

### 2. 本地前端开发预览
```powershell
npm install
npm run dev
```
打开 `http://127.0.0.1:5173/`。浏览器模式使用内置 Mock 演示数据，仅用于调试 React 界面，不会读写本机真实桌面。

### 3. 本地构建并运行桌面端
```powershell
npm run desktop
```
该命令会自动编译前端生产资源、调用系统 .NET 编译器编译 C# 原生宿主，并启动生成的 `PivKeyUBox.exe`。

---

## 打包与发布

### 1. 构建绿色便携包
```powershell
npm run package
```
产物将输出在 `release/` 目录：
- `release\PivKeyUBox\PivKeyUBox.exe`（主程序目录）
- `release\PivKeyUBox-win-x64.zip`（便携版压缩包）

### 2. 构建 Inno Setup 安装包
```powershell
npm run installer
```
产物将生成在 `release/` 目录：
- `release\PivKeyUBox-Setup-v0.1.0.exe`（单文件向导式安装包，体积约 8MB）

打包脚本会自动定位本地已安装的 Inno Setup 编译器（`ISCC.exe`），将当前所有依赖、配置文件与资源压缩打包为符合 Windows 规范的安装程序。

---

## 在线更新与 GitHub Releases 发布流程

客户端原生支持通过 GitHub Releases 进行自动版本检测与应用内升级：

### 发布新版本流程
1. **修改版本号**：在 `package.json` 中更新版本号（例如修改为 `"version": "0.2.0"`）。
2. **生成安装包**：在终端运行 `npm run installer`，将在 `release/` 目录下生成 `PivKeyUBox-Setup-v0.2.0.exe`。
3. **前往 GitHub 创建 Release**：
   - 打开 [GitHub Releases 页面](https://github.com/PivKeyU/PivKeyUBox/releases)。
   - 点击 **Draft a new release**。
   - Tag 填写 `v0.2.0`（建议带 `v` 前缀）。
   - 填写版本更新日志（Markdown 格式，客户端会自动解析并展示）。
   - 将生成的 `PivKeyUBox-Setup-v0.2.0.exe` 上传为 Release 附件。
   - 点击 **Publish release** 发布。

### 客户端检查与升级
- 用户打开客户端设置面板的 **「关于与更新」** 选项卡。
- 点击 **「检查更新」** 按钮，程序将请求 `https://api.github.com/repos/PivKeyU/PivKeyUBox/releases/latest`。
- 若检测到新版本，界面将直接展示版本号、更新日期与更新日志，并提供 **「下载更新安装包」**。
- 下载完成后点击 **「立即重启并安装更新」**，安装包将启动并自动平滑覆盖现有文件，无需手动下载替换。

---

## 项目结构

```text
PivKeyUBox/
├── native/                         # Windows 原生宿主 (C# 5 / WPF)
│   ├── PivkeyHost.cs               # 主窗口与桌面注入宿主、WebView2 容器通信
│   ├── Manager.cs                  # 分区管理、分类状态树、配置原子持久化
│   ├── UpdateManager.cs            # GitHub Releases 在线检测与更新下载引擎
│   ├── PanelWindow.cs              # 独立桌面收纳分区原生窗口实现
│   ├── NoteWindow.cs               # 桌面便签与待办清单原生窗口
│   ├── WidgetLayer.cs              # 桌面 HWND 挂载与 Z-Order 管理协议
│   ├── LayerSession.cs             # 唤起会话与前台感知回落监视器
│   ├── NativeFileOps.cs            # Windows 原生 IFileOperation 文件桥
│   └── ShellMenu.cs                # 原生桌面/分区上下文菜单助手
├── src/                            # React 19 设置与界面层
│   ├── components/                 # 设置面板、关于与更新、图标选择器
│   ├── services/                   # 原生 RPC 桥、文件分类器与监听
│   ├── state/                      # 布局模式、偏好持久化状态管理
│   └── styles.css                  # Fluent UI / Liquid Glass 样式
├── scripts/                        # 构建与打包自动化工具链
│   ├── build-windows.ps1           # 原生宿主与便携包编译脚本
│   ├── build-installer.ps1         # Inno Setup 自动化编译脚本
│   └── installer.iss               # Inno Setup 6 安装包配置脚本
└── release/                        # 构建输出目录（由 .gitignore 忽略）
```

---

## 安全约定与可靠性保障

- **文件只移不删**：手动“立即收纳”会先显示待移动清单并等待用户确认；自动收纳仅在用户主动启用后处理状态稳定的已匹配文件；托盘与高级设置均支持多批次撤销。
- **同名防覆盖保护**：遇到同名目标自动追加 ` (1)`、` (2)` 等后缀，绝不覆盖已有文件。
- **配置原子保存与快照自愈**：主配置保存时采用原子写入与临时交换，并在每次成功落盘后保留 `config.json.bak` 快照；配置损坏时自动从快照无感恢复。
- **更新覆盖安全性**：安装器安装到用户专属空间（免 UAC 提权），升级与卸载均完全保留用户已设置的分类规则、便签与收纳数据。
