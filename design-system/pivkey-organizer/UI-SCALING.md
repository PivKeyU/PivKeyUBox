# 片刻收纳 · 缩放、字号与图标清晰度

- 编制日期：2026-09-20
- 对应提交：`24a3e24`（界面缩放生效 / 图标像素档位 / 小工作区布局）+ `4cfc13e`（右停靠负 x 修复）
- 适用版本：`PivKeyUBox.exe` 打包版（原生 `native/*.cs` 绘制桌面面板）+ React 设置页（`?view=settings`）

---

## 一、这份文档解决什么

桌面面板的**每一处尺寸**都要同时经过两个独立缩放源：

1. 用户在设置页里拖的「界面缩放」（`uiScale`，80–130，默认 100）；
2. Windows 显示设置的缩放（`dpiScale`，100% / 125% / 150% / 200% …）。

在 `24a3e24` 之前，面板只吃第二个、完全不吃第一个：设置页的「界面缩放」滑块对收纳栏没有任何效果（根因见 §二）。同时图标被硬编码成固定像素画布，在放大后发软；布局预设用虚构的下限把视口撑大，在小工作区里算出越界坐标。

**本文是「界面缩放 / 字号基准 / 图标像素档位 / 三档预设」的唯一权威说明。**
改动任何 `FontSize`、`Width`、`Dip()` 调用或图标解码尺寸之前，先读完 §二。

**适用范围**：`native/PanelWindow.cs`（收纳面板）、`native/PivkeyHost.cs`（图标生成与磁盘缓存）、`native/Manager.cs`（布局预设与同步负载）。便签窗口（`NoteWindow.cs`）目前**没有**接入 `uiScale`，其字号仍是写死的 DIP 值。

---

## 二、术语与换算契约（最重要的一节）

### 2.1 三个量

| 名称 | 含义 | 取值 | 谁设置 |
|---|---|---|---|
| **DIP** | Device Independent Pixel（设备无关像素）。WPF 的 `Width`、`Height`、`FontSize`、`Thickness` 单位都是 DIP | 逻辑值 | 代码 |
| **`uiScale`** | 应用级界面缩放百分比 | 80–130，默认 100，步进 5 | 设置页「界面缩放」滑块 |
| **`dpiScale`** | 当前显示器 DPI 缩放比 = `GetDpiForWindow(hwnd) / 96` | 通常 1.0 / 1.25 / 1.5 / 2.0 | Windows 显示设置 |

### 2.2 两条换算公式

```
逻辑尺寸（DIP）   dip(x)  = x * uiScale / 100
位图像素（物理）  iconPx(x) = ceil(x * uiScale/100 * dpiScale / 16) * 16   →  夹到 [48, 256]
```

### 2.3 ⚠️ 警告：逻辑尺寸绝不能乘 `dpiScale`

**WPF 已经自动把 DIP 换算成物理像素。** 你写 `Width = 30`，在 150% DPI 上 WPF 自己就会画成 45 物理像素。所以：

- ✅ 逻辑尺寸只乘 `uiScale` → `Dip(x) = x * uiScale / 100`
- ❌ 逻辑尺寸再乘 `dpiScale` → **重复缩放**

**改错了会怎样（反例）**：假设有人"热心"地把 `Dip()` 改成 `x * uiScale / 100 * dpiScale`，在 150% DPI 的机器上：

| 元素 | 正确（只乘 uiScale） | 错误（又乘了 dpiScale） |
|---|---|---|
| 折叠标题条高（uiScale=100） | 30 DIP → 屏幕 45 px | 45 DIP → 屏幕 **67.5 px** |
| 面板最小宽度 | 230 DIP | **345 DIP** |
| 面板最小高度 | 150 DIP | **225 DIP** |

后果不是"稍微大一点"，而是：标题条比标题文字大 50%，面板最小尺寸超出小工作区的实际可用空间，`Manager` 的布局预设算出的坐标全被窗口钳制 → 面板挤压、重叠、越界。这正是 `24a3e24` 之前小屏 + 高缩放下的症状，修的就是这一类"乘以真实尺寸而不是逻辑尺寸"的错误。

**唯一需要乘 `dpiScale` 的地方是"我要一张 N 物理像素的位图"** —— 因为位图不存在"DIP"概念，WPF 拿到 96px 的位图要铺在 96 DIP 的框里，在 150% DPI 上就会被拉成 144 物理像素而发软。这就是 §四 的 `ResolveIconPixelSize`。

### 2.4 谁负责消费 `uiScale`

| 消费端 | 是否已接 | 说明 |
|---|---|---|
| `PanelWindow`（收纳面板） | ✅ | `Dip()` + `ResolveIconPixelSize` |
| `PivkeyHost`（shell 图标生成/磁盘缓存） | ✅ | `ApplyUiScale` → 失效图标缓存，下次扫描按新档位重建 |
| `Manager`（布局预设 / 同步负载） | ✅ | 只负责把 `preferences.UiScale` 写进 `PanelSyncData`，自己不做尺寸换算 |
| React 设置页（`App.tsx` 的 `uiZoom`） | ✅（此前已有） | 设置页自己的 `zoom`，与面板互不干扰 |
| `NoteWindow`（便签） | ❌ | 仍用写死的 DIP 字号，未接 `uiScale` |

---

## 三、字号基准表（uiScale = 100 时的 DIP 值）

原生面板所有长期存活控件在 `ApplyScaleMetrics()` 里统一刷新；菜单每次右键重建，构建时直接 `Dip()`。

| 元素 | 基础 DIP | 代码位置 |
|---|---|---|
| 分区标题 | 14 | `PanelWindow.cs` `title.FontSize = Dip(14)` |
| 计数徽标 | 11 | `count.FontSize = Dip(11)` |
| 标题栏按钮图标 | 15 | `ApplyHeaderIconScale` → `Dip(15)` |
| 面板内搜索框 | 11 | `searchBox.FontSize = Dip(11)` |
| 文件名标签 | `labelSize`（默认 **12**，范围 **8–18**） | `ApplyItemLabelFontSize()` / `RebuildItems()` |
| 文件名标签（列表视图） | `max(10, labelSize + 1.5)` | 同上 |
| 重命名输入框 | 13 | `BeginRename()` → `Dip(13)` |
| 右键菜单项 | 13 | `StyledMenuItem` → `Dip(13)` |
| 子菜单项 | 12.5 | 子菜单构建 → `Dip(12.5)` |
| 折叠标题条高 | 30 | `HeaderBarHeight` |
| 胶囊边长 | 56 | `CapsuleSize` |

补充说明：

- 标题栏按钮**热区**边长 = `clamp(Dip(24), 16, 缩放后标题条高度 - 2)`，保证按钮不会顶破标题条。
- 条目标签的行高与最大宽高同步走 `Dip()`，否则放大后的两行文字会被 `MaxHeight` 裁掉。
- 菜单项是"每次右键重新构建"的短命控件，所以直接在构建时 `Dip()` 一次即可，不进 `ApplyScaleMetrics()`。

---

## 四、图标像素档位

### 4.1 `ResolveIconPixelSize`

```csharp
internal static int ResolveIconPixelSize(double logicalSize, double uiScalePercent, double dpiScale)
// raw = logicalSize * uiScalePercent/100 * dpiScale
// 向上取 16 的倍数，并夹到 [48, 256]；非法输入一律回落到安全默认（logical→54, uiScale→100, dpi→1.0）
```

向上取 16 倍数是为了**跨缩放档位复用同一份位图**（避免每次滑动滑块都产生一张新图），夹取范围保证最小 48px（不会糊）与最大 256px（不会爆内存）。

### 4.2 常见组合的输出

| logical | uiScale | dpiScale | raw | `ResolveIconPixelSize` |
|---|---|---|---|---|
| 54 | 100 | 1.0 | 54.0 | **64** |
| 54 | 100 | 1.5 | 81.0 | **96** |
| 54 | 130 | 1.5 | 105.3 | **112** |
| 54 | 80 | 1.0 | 43.2 | **48** |
| 54 | 100 | 2.0 | 108.0 | 112 |
| 54 | 130 | 2.0 | 140.4 | 144 |
| 64 | 100 | 1.5 | 96.0 | 96 |
| 72 | 130 | 2.0 | 187.2 | 192 |

（前四行是实机/单测确认的返回值；后四行由同一公式推出。）

### 4.3 与旧实现的零回归

改动前固定用 **96px 画布** + 内缩 6px → 绘制区 `(6, 6, 84, 84)`。

改动后 `IconToDataUrl(iconHandle, targetPx)` 的内缩规则是 `padding = max(2, canvas / 16)`：

| targetPx | padding | 绘制区 |
|---|---|---|
| **96** | 96 / 16 = **6** | **84 × 84** ← 与改动前完全相同 |
| 64 | 4 | 56 × 56 |
| 48 | 3 | 42 × 42 |
| 112 | 7 | 98 × 98 |
| 256 | 16 | 224 × 224 |

即：在默认档位（uiScale=100、DPI=100% 的图标逻辑尺寸下）产出与旧实现**逐像素一致**的图，只有真正需要更高分辨率时才换档位。

**磁盘图标缓存**使用版本前缀 `hires-v3:e<epoch>:`（`GetIconCacheFilePath`）。`epoch` 随 `uiScale` 变化递增，因此不同档位的图标互不覆盖、也不会命中上一档的旧文件。

---

## 五、三档预设（清晰 / 标准 / 紧凑）

设置页「收纳」页新增，作用于**当前选中的分类**（`onItemLayoutChange(selectedCategory, …)`），不批量写其他分类。

| 预设 | `labelSize` | `iconSize` | `itemSize` | `gap` |
|---|---|---|---|---|
| 清晰 | 14 | 64 | 92 | 10 |
| 标准 | 12 | 54 | 76 | 6 |
| 紧凑 | 10 | 44 | 62 | 4 |

- 预设判定是"四项全等才高亮"：任意一项被滑块改动后，预设按钮回到未选中态，用户看到的是自己调出来的组合。
- 「名称字号」滑块上限同步放宽到 **18**（原 14）。
- 右键菜单里的「调整项目显示大小」子菜单提供的是另一组档位（紧凑 60/40/9、标准 76/54/10、大 92/64/11、超大 108/72/12），与设置页预设**不互相高亮**。

---

## 六、旧配置兼容政策

**用户已保存的自定义值优先，只在缺失时才使用新默认值。**

实现载体是 `boundedNumber(value, fallback, min, max)`：值是有效有限数字就用它并夹到新区间，否则才回落到 `fallback`。

| 项 | 旧默认 | 新默认 | 新区间 | 旧值 8–11 会怎样 |
|---|---|---|---|---|
| `labelSize`（前端 `normalizeItemLayout`） | 10 | 12 | [8, 18]（原 [8, 14]） | **原样保留**，不被强制改成 12 |
| `labelSize`（原生 `PreferenceItemLayout` / `PanelWindow.UpdateContent`） | 10 | 12（紧凑模式 11） | [8, 18] | 原样保留 |
| 右键滚轮调整字号 | 夹到 14 | 夹到 18 | — | — |

**守护这条政策的测试**（`src/state/preferences.test.ts`）：

- `it('defaults the file name label size to 12')`
- `it('keeps an existing label size instead of forcing the new default')` ← 直接断言旧值 8–11 不被覆盖
- `it('accepts the widened label size range and clamps outside values')`
- `it('preserves a custom label size for each panel')`

---

## 七、本次改动清单

| 文件 | 改动 |
|---|---|
| `native/PanelWindow.cs` | 新增 `uiScale` / `dpiScale` 字段、`Dip()` 与 `ScaledHeaderBarHeight()` / `ScaledCapsuleSize()`；`ApplyScaleMetrics()` 统一刷新长期控件字号与胶囊几何。图标改为按 `IconTargetPx()` 生成与解码，解码缓存键含档位。`GetLayoutViewport()` 去虚构下限；`GetShellThumbnail` 无关。新增 `narrowHeader` 窄标题条。`labelSize` 默认 12、上限 18；右键菜单补「面板内搜索」「调整项目显示大小」。 |
| `native/PivkeyHost.cs` | `PanelSyncData.uiScale` 字段；`ApplyUiScale()` 在缩放变化时失效内存/磁盘图标缓存；`ResolveIconPixelSize()`；`IconToDataUrl(handle, targetPx)`；`GetShellIcon*` 改为实例方法并传档位；`GetIconCacheFilePath(path, epoch)`；`GetDpiForWindow` P/Invoke。 |
| `native/Manager.cs` | `PanelSyncData.uiScale = preferences.UiScale`；`BuildPanelKey()` 签名加入 `uiScale`（缩放变化即触发重推）；`PanelMinWidth/PanelMinHeight` 保持 230/150；`BuildPanels` 线路修正；`LabelSize` 默认 12、夹取上限 18。 |
| `src/state/preferences.ts` | `defaultItemLayout.labelSize` 10 → 12；`normalizeItemLayout` 区间 [8,14] → [8,18]。 |
| `src/utils/panelSync.ts` | `buildPanelSyncKey()` 的签名项加入 `panel.uiScale`。 |
| `src/hooks/usePanelSync.ts` | 同步 payload 携带 `uiScale`；effect 依赖列表加入 `preferences.uiScale`。 |
| `src/utils/panelLayout.ts` | `createDockLayout` 补「最小占位宽度检查」（负 x 兜底，与原生 `Manager.cs` 同型）。 |
| `src/components/SettingsModal.tsx` | 新增「清晰 / 标准 / 紧凑」三档预设（作用于当前分类）；「名称字号」滑块上限 14 → 18；预览卡提示文案同步。 |

（`src/utils/panelLayout.ts` 的负 x 修复实际分两笔提交：`24a3e24` 修原生 `Manager.cs`，`4cfc13e` 修前端同型实现。）

---

## 八、小工作区布局（附：为什么必须去掉虚构下限）

### 8.1 视口对照

旧实现在 `GetLayoutViewport()` 里对工作区做 `Math.Max(800, …)` / `Math.Max(560, …)`。真实工作区在"高缩放 + 小屏"下可能远小于这个数，视口被撑大后预设按虚构空间排版，坐标越界 → 被各窗口自己的钳制逻辑压缩 → 挤压/重叠/越界。

| 真实工作区（DIP） | 旧视口 | 新视口 |
|---|---|---|
| 1920 × 1017 | 1920 × 1017 | 1920 × 1017 |
| 1366 × 728 | 1366 × 728 | 1366 × 728 |
| 1093 × 614 | 1093 × 614 | 1093 × 614 |
| 960 × 540 | 960 × **560**（被撑高） | 960 × 540 |
| 683 × 364 | **800 × 560**（双轴撑大） | 683 × 364 |

新下限只保留防御语义：宽度 `max(230 × 2 + 48, WorkAreaWidth)`、高度 `max(200, WorkAreaHeight)`，用于兜住工作区返回 0 之类的异常值。

> **注意**：React 侧 `src/App.tsx` 的 `applyLayoutPreset` / `applyFolderAlignment` 里仍写着 `Math.max(800, window.innerWidth / layoutZoom)` 与 `Math.max(560, …)`，但该分支只在非原生运行时（开发时的 Web 预览）真正执行 —— 打包版的面板布局由 `Manager.cs` 的预设计算。也就是说：**打包版已不受 800×560 影响，Web 预览路径仍是旧行为**（要在 Web 里看到同一结果，需同步这两个函数）。

### 8.2 `createDockLayout` 的负 x

窄视口下"右停靠"预设的列数由高度决定，而可用宽度已放不下这么多列 → 最左几列算出负 x。该缺陷在旧的 800×560 视口下就已存在，去掉下限后更容易触发。

- 实测（683 × 364 / 8 个分区）：修复前最左 x = **−285**，修复后所有 x ≥ `margin`。
- 兜底写法与 `corners` / `balanced` 预设一致：所需最小宽度超过可用宽度时退回居中网格。
- **两个文件是同型实现**：`native/Manager.cs` 的 `CreateDockLayout` 与 `src/utils/panelLayout.ts` 的 `createDockLayout` 都必须有这段检查，只修一个会留下不一致。
- 回归测试：`src/utils/panelLayout.test.ts` → `it('never places dock panels at negative x in a narrow viewport')`。

---

## 九、窄标题条

| 条件 | 行为 |
|---|---|
| 面板宽度 ≥ 200 DIP | 正常：四个工具按钮（搜索 / 尺寸 / 视图 / 图钉）按 hover 状态显示 |
| 面板宽度 < 200 DIP | 只留 标题 + 折叠 + 菜单；四个工具按钮**彻底收起**（hover 也不出） |

等价入口并入右键菜单：「面板内搜索」与「调整项目显示大小」子菜单（后者与尺寸按钮弹出的是同一组档位）。折叠按钮与图钉按钮的状态（`showSearch` / `showPin`）在窄态下同样被抑制。

---

## 十、性能与缓存

| 项 | 值 / 行为 |
|---|---|
| 解码缓存上限 | 32 MB |
| 解码缓存低水位 | **24 MB**（超出上限后一次删到 24 MB 才停，滞回，避免边界反复逐出） |
| 解码缓存键 | `targetPx + "|" + iconUrl` —— 档位不同即不同条目，跨 DPI 后按新档位重建 |
| 后台解码去重 | `pendingIconDecodes`：同一 key 只排队一次 |
| 后台解码并发 | `Semaphore(4, 4)`：同时最多 4 个 |
| 磁盘图标缓存 | 文件名 hash 前缀 `hires-v3:e<epoch>:`，`epoch` 随 `uiScale` 变化递增 |
| 触发缓存失效 | `PivkeyHost.ApplyUiScale()`（缩放变化）、`PanelWindow.UpdateContent()` 检测到 `uiScaleChanged` 时刷新胶囊 data URL |

---

## 十一、已知取舍 / 未做的部分

诚实记录，以下都是**确实没做**的：

| # | 未做的部分 | 影响 |
|---|---|---|
| 1 | `ResizeMinWidth = 230` / `ResizeMinHeight = 150` **未随 `uiScale` 缩放** | 「界面缩放」拖到 130 时，最小尺寸按 DIP 语义是正确的，但用户手动拖到最小后的**视觉**比例不变（不会跟着放大） |
| 2 | `narrowHeader` 的 **200 DIP 阈值未随 `uiScale` 缩放** | 判断用的是缩放后的 `Width`。uiScale=130 时 200 DIP 对应更多内容宽度，窄态触发点相对更"晚" |
| 3 | `CapsuleRadius`（12）/ `PanelRadius`（8）**圆角未缩放** | 缩放后圆角在视觉上相对变小；胶囊折叠态与展开面板的圆角比例会略微漂移 |
| 4 | `GetShellThumbnail` 仍是 `static` 且硬编码 192px | 图片文件的缩略图路径（`SHCreateItemFromParsingArgs`）没有接入档位，高 DPI 下仍按 192px 请求 |
| 5 | 跨屏拖拽导致 DPI 变化时，图标**不会立即**按新 DPI 重算 | 要等下一次 `uiScale` 变化或下一轮扫描；`RestoreSyncedBounds()` 里会 `RefreshDpiScale()`，但只影响之后的投递 |
| 6 | 设置页三档预设只作用于**当前选中分类** | 没有「一键应用到全部分类」；多个分类需要逐个切过去点 |
| 7 | 便签（`NoteWindow.cs`）未接 `uiScale` | 便签字号仍是写死的 DIP 值，缩放面板时便签不跟随 |

---

## 十二、验证方式

### 12.1 自动化

```powershell
npm test                                # vitest：6 个文件 / 45 个用例
npm run build                           # tsc -b + vite build
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-windows.ps1   # csc /langversion:5
```

本次相关用例：

| 文件 | 用例 | 断言 |
|---|---|---|
| `src/utils/panelSync.test.ts` | `changes when the ui scale changes so the native panel is re-pushed` | `uiScale` 变化 → 同步键变化（证明会触发重推） |
| `src/state/preferences.test.ts` | `falls back to 100 when missing or invalid` | `uiScale` 缺省 100 |
| `src/state/preferences.test.ts` | `keeps a user value inside 80-130 and clamps outside values` | 80–130 夹取 |
| `src/state/preferences.test.ts` | `keeps the shipped default at 100` | 默认值不变 |
| `src/state/preferences.test.ts` | `defaults the file name label size to 12` | 新默认 |
| `src/state/preferences.test.ts` | `keeps an existing label size instead of forcing the new default` | 旧值不被覆盖 |
| `src/state/preferences.test.ts` | `accepts the widened label size range and clamps outside values` | 新区间夹取 |
| `src/utils/panelLayout.test.ts` | `never places dock panels at negative x in a narrow viewport` | 窄视口右停靠无负 x |

### 12.2 实机测量手法

**折叠标题条高度**（验证 `Dip()`）：把面板切到折叠态，用 Windows 放大镜 / 截图工具取像素高度。已实测（1920×1080 @150% DPI）：

| uiScale | 实测折叠标题条 | 期望 |
|---|---|---|
| 100 | 30 px | 30 ✓ |
| 130 | 39 px | 30 × 1.3 = 39 ✓ |

胶囊边长同样随缩放：uiScale=130 时 56 → **73 px**（56 × 1.3 = 72.8）。

**面板窗口尺寸枚举**：`EnumWindows` + `GetWindowRect` 枚举 PivKeyUBox 的窗口，读出每个面板的物理像素尺寸，与 `Dip()` 的期望值比对。这是判断"到底谁没生效"最快的办法 —— 缩放没生效时窗口尺寸恒定，缩放过了头（重复乘 DPI）时尺寸是期望值的 1.5 倍。

**图标清晰度**：在同一屏内并排比较 uiScale=100 与 uiScale=130 的图标，或直接放大截图看边缘是否有插值糊边。

---

## 十三、改动守则（给后来者）

1. 新增任何逻辑尺寸，一律走 `Dip(x)`；不要写裸数字。
2. 新增任何位图请求，一律走 `ResolveIconPixelSize(logical, uiScale, dpiScale)`；不要写死像素。
3. 在 `Dip()` 里加 `dpiScale` 之前，重读 §2.3。
4. 改 `labelSize` 默认值前，先确认 `preferences.test.ts` 里"旧值不被覆盖"的用例仍然通过。
5. `native/Manager.cs` 与 `src/utils/panelLayout.ts` 的预设实现是同型的，改一个必须同步改另一个。
