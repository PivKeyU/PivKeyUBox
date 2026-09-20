# 片刻收纳 · UI 风格对齐审查报告

- 审查日期：2026-09-17
- 审查范围：`src/styles.css`、`tokens.css`、`src/components/*`、`native/*.cs`、`design-system/pivkey-organizer/*`、`design-review/*`
- 当前验证状态：`npm test` 36 passed；`dist/` 为 2026-09-17 19:53 产物（构建可用）
- 结论口径：本报告只做审查与定位，未修改任何代码
- **后续状态（2026-09-20）**：`24a3e24` / `4cfc13e` 已完成一批 UI 清晰度修复（界面缩放生效、图标像素档位、小工作区布局、文件名标签默认字号 10→12、设置页三档预设）。本报告中与之冲突的段落已在原位加 **> 2026-09-20** 标注；其余未标注的条目仍按审查日的现状有效。当前 `npm test` 为 6 文件 / 45 用例全通过。

---

## 一、总体结论

**风格没有对齐，而且不是"细节偏差"，是"没有单一真相源"级别的结构问题。**

具体表现为三层各自成体系：Web 设置页跑着一套 iOS 中性 + Chiikawa 暖色的双调色板，原生桌面窗口（面板/便签）跑着一套以 `#3478f6` 为默认的 iOS 语义色，托盘与右键菜单又在第三套数值上；而项目里同时存在四份互相矛盾的"设计规范"文档（`design-system/MASTER.md` 写的是青绿 `#0D9488` + 橙色 `#EA580C` + Plus Jakarta Sans，与这个产品实际的蓝色 `#3478f6` + 系统字体毫无关系）。

同时，**产品对外宣称的视觉方向（吉伊系奶油纸张 + 珊瑚强调）只覆盖了一部分界面**：真正的奶油色调只剩 `--color-ink` 派生文字和图标选择器卡片两处，其余已经被一轮"DeskBox 卡片皮肤 v3"的中性灰取代；而最新加入的便签模块则完全没走设计系统，用 35 处内联样式和 3 个 emoji 独立成形。

重度问题上，我按 P0（用户直接可见的风格断裂）/ P1（同一控件数值互斥、维护代价高）/ P2（可访问性与资产开销）分级，共 22 条。

值得肯定的是：核心 36 个单测全绿，`Modal` 的焦点陷阱与 Esc 处理是完整的，`prefers-reduced-motion` 有全局兜底，面板与便签两个手绘窗口确实接入了主题/亚克力/强调色，`tokens.css` 本身的命名令牌设计（即使现在有 3 个零引用）方向是对的。问题不在工程质量，而在"没有一个人/一层能宣布'这个值就是最终值'"。

---

## 二、量化体检表

| 维度 | 实测 | 说明 |
|---|---|---|
| CSS 总行数 | 4786 行（122 KB 产物） | 由 8 轮改版叠加而成，含 2 个重复编号的"§14" || 字号取值 | **17 个**（10 / 10.5 / 11 / 11.5 / 12 / 12.5 / 13 / 13.5 / 14 / 16 / 17 / 18 / 20 + 4 个 clamp） | 其中 5 档（11–13px）挤在 2px 区间 |
| 走令牌的字号 | **0 / 145** | `font-size` 全部为字面量 |
| 圆角取值 | **24 个**（3–20px + 999px + 50%） | 走令牌 27 / 159 处（`--radius-sm/md/lg`） |
| 硬编码颜色 | **175 个 hex**（71 种） | 其中 `#3478f6` 出现 **64 次**，且 64/64 都是 `var(--theme-accent, #3478f6)` 的兜底值 |
| 色彩声明走令牌比例 | 667 / 810 | 181 处为纯字面量 |
| 间距声明 | 481 处，313 处为纯 px | `var(--space-*)` 引用 144 次 |
| `!important` | **48 处**（44 处在 P7 层） | 判断：22 处必要、25 处冗余、1 处有害 |
| 动效时长字面量 | 20 处硬编码 ms | 弹窗/Toast/进场动画整组绕过 `--dur-*` |
| 零引用令牌 | `--r-1` `--r-2` `--r-3` `--t-slow` `--radius-xl` `--color-neutral` `--color-negative` | 7 个 |
| 无消费者的选择器 | 7 个（如 `.settings-brand-card`、`.rule-editor__pattern`、`.settings-note--success`） | 另有 `.compact-view` 在 `App.tsx:559` 上类但 CSS 里没有任何规则 |
| 打包资产 | 6 张 **1254×1254 / 约 1 MB** 的角色 PNG = 6.2 MB | `dist/assets/` 实测；实际显示尺寸 36–46 px |
| 仓库内死资产 | `src/assets/fonts/` **79 MB** 的 Maple Mono TTF | Web 侧已不再引用，原生侧找不到它们 |
> **2026-09-20 补充**：本表描述的是 `src/styles.css`（设置页）的现状，**不受** `24a3e24` 影响——那次改动全部落在原生层与状态层，没有动 CSS 与本表的任何数字。唯一交叉的是第 4 节列出的 `UiScale` 未进 `PanelSyncData`（已修，见 §四.10 标注）。

---

## 三、P0：用户直接可见的风格断裂

### 1. 便签模块完全脱离设计系统

`SettingsModal.tsx:509-661` 的"便签与待办"页是唯一一处不参与任何主题体系的界面：

- **单页 23 处内联样式**（`SettingsModal.tsx:517`–`658`），占全项目 `style={{` 总数 35 处的三分之二，包括按钮、卡片、空态、贴士栅格；
- **emoji 当图标**：`💡 托盘随时新建`（`:642`）、`⚡ 分区右键就近创建`（`:648`）、`📌 便签内直接新建`（`:654`）——`design-system/pivkey-organizer/pages/chiikawa-review.md` 明确写了"正式接入时不混用 emoji"；
- **硬编码语义色**：`#3478f6`（`:588`）、`#d97706`（`:588`）、`#dc2626`（`:610`），其中橙色 `#d97706` 不在任何令牌表里；
- **局部几何**：`borderRadius` 4 / 6 / 8 / 10 混用，`fontSize` 11 / 12 / 13 混用，`background: 'rgba(0,0,0,0.02)'` 与 `1px dashed rgba(0,0,0,0.1)` 是自造的表面；
- 颜色回退写成 `var(--chi-muted, #888)` / `var(--chi-muted, #666)`（`:556`、`:643`、`:649`、`:655`），同一语义两个不同的兜底值。

这是最近新增的功能（`NoteWindow.cs` 9-17 还在改动），所以它反映的是"新增界面没有可复用的样式契约"，比数值本身更值得处理。

### 2. 四套调色板并存，且文档与实现互相矛盾

| 来源 | 强调色 | 纸面 | 字体 | 与实现的关系 |
|---|---|---|---|---|
| `tokens.css:11` + `src/state/theme.ts:2` | `#3478f6` | `#ffffff` | 未定义 | **实际默认**（`Manager.cs:335` 同为 `#3478f6`） |
| `styles.css:2689-2702` Chiikawa | `--chi-coral #e98687` | `#fff9f0` | — | 仅剩局部生效（见第 4 条） |
| `styles.css:3582-3608` `--st-*` | 借用 `--settings-accent` | `#ffffff` / `#2b2825` | — | 设置页容器与次要文字的实际来源 |
| `design-system/MASTER.md` | `#0D9488` 青绿 + `#EA580C` 橙 | `#F0FDFA` | Plus Jakarta Sans | **与产品无关**，纯文档噪音 |

`MASTER.md` 是自动生成的产物（`Generated: 2026-08-18`），内容是一套通用 SaaS 落地页规范（还包含 "Hero > Product video > CTA" 的页面模式），对桌面工具没有任何指导意义，但它以 "Design System Master File" 的名义大于 `design-review/` 下的真实方向文档，后续开发者会先读到它。

同一层级还有一处方向性矛盾：产品审阅稿定义的是"奶油 + 珊瑚"（`design-review/README.md`），而实际默认渲染是中性 iOS 蓝；`--chi-coral` 的取值 `#e98687` 也不等于审阅稿里的 `--review-coral #EF8D86`，`--chi-muted #786562` 不等于 `--review-muted #8D7770`。

### 3. 三个辅助窗口不支持深色主题

深色模式下会弹出纯浅色窗口的几个原生界面：

| 窗口 | 证据 | 后果 |
|---|---|---|
| 新建分类对话框 | `CategoryDialog.cs:35-45` 全程未设 `Background`；文字用暖色 `#37322A`（`:99`、`:135-136`） | 深色桌面上系统默认白底 + 暖棕文字 |
| 快速搜索 | `QuickSearchWindow.cs:43` 米色 `#FFFDF7`、`:52` 深棕文字，全文件无条件分支 | 同上 |
| 设置窗口外壳 | `SettingsWindow.cs:47`、`:51` 两处 `#F3F3F3` | 深色下开窗先闪白；而 `PanelWindow.cs:2918` 的注释里已经写明中性面应当成对出现 `#F3F3F3 / #1F1F1F` |

对照之下，桌面面板（`PanelWindow.cs:1464`）与便签（`NoteWindow.cs:818`）都正确接收了主题与强调色载荷，说明管线是通的，只是这三个窗口创建时没接（`PivkeyHost.cs:1325`、`:1437`）。

### 4. 设置页在深色/浅色下都是"双调色板马赛克"

同一屏里文字颜色来自两个体系：

- 容器与次要文字来自 `--st-*` 中性灰（`styles.css:3735`、`:3740`、`:4081`、`:4121`），深色下是 `#f0e9e2` / `#a89c94`；
- `.settings-content` 根文字、`.settings-command` 文字仍走 `--color-ink`，而它被重映射到 Chiikawa 的 `#3d302c`（浅色）/ `#fff6ef`（深色）（`styles.css:1836-1858`、`:2996`、`:3121`）。

实测在 `styles.css:3576` 之后，`var(--line)` / `var(--line-strong)` / `var(--text-*)` 的引用次数为 **0**，即暖色体系只在 v3 皮肤没覆盖到的地方"漏"出来。图标选择器是唯一一个漏掉中性化的卡片族：`.icon-picker__grid button` 仍用 `--chi-rule` + `rgba(255,249,243,.72)`，且 `.is-active` 的珊瑚描边 `--chi-coral-deep` 在深色分支里也没有被替换（`styles.css:2841-2854`）。

---

## 四、P1：同一控件的数值互斥与维护代价

### 5. 五轮改版叠加，一个控件的最多被重写 7 次

`styles.css` 的分层顺序是 P0（§1–§15 基础）→ P1（§16 重排）→ P2（Hallmark 工作台）→ P3（Chiikawa）→ P4/P4b（Index-First + 溢出修复）→ P5（DeskBox 卡片皮肤 v3）→ P6（parity polish）→ P7（自适应系统，4047-4786）。后一层普遍只写"覆盖"，不删旧值：

| 选择器 | `border-radius` 演进（行号） | 生效值 |
|---|---|---|
| `.settings-section` | 16 (670) → 18 (1614) → 18 (2812) → 0 (2002) → 0 (3074) → 10 (3688) → **12 !important (4088)** | 12 !important |
| `.settings-section` | padding：`6px 0 8px` → `space-6 0` → `18px 0 14px` → `space-5 0` → `0 14px 4px` → **`16px 20px !important`** | 4087 |
| `.switch-row` | min-height：52 → 56 → 52 → 46 → **52** | 3964 |
| `.settings-nav button` | min-height：38 → 44 → 44 → 42 → 36 → **38** | 3913 |
| `.segmented button` | min-height：30 → 34 → 44 → 40 → **30** | 3775（绕回起点） |
| `.modal__header h2` | font-size：16 → 20 → clamp(18–22) → 18 → **16** | 3616（同样绕回起点） |
| `.advanced-input` | height：32 → 44 → **35 !important** → `.rule-row` 内 **31 !important** | 4299 靠 !important 压过 4200 的 !important |

`.modal__header h2` 与 `.segmented button` 走了一圈回到起点，说明这五轮里至少有两轮是在"来回改同一个值"，而不是在推进设计。这类叠加没有净收益，只有阅读成本和互相抵消的 `!important`。

### 6. `!important` 被当成布局工具

48 处中 44 处集中在最新的 P7 层。**1 处有害**：`styles.css:4055` 的 `padding: … 96px !important` 压掉了 `is-compact`（3513）与移动端（1819）的内边距，紧凑窗口底部永远留 96px 空白；配套的 `--settings-footer-clearance`（3531，4rem）只活在 `scroll-padding-block` 里，滚动锚点与实际内边距差 32px。

**其余 7 处**（`4657-4662`、`4697-4698`、`4703-4704`、`4709-4711`）用 `!important` 压过 `is-compact` 的单列回退，等于在最新一层亲手取消了窄窗口适配。

### 7. 字号没有级差

17 个取值里，`11 / 11.5 / 12 / 12.5 / 13` 五档集中在 2px 内，且全部是字面量（145 处 `font-size`，0 处走令牌）。用户肉眼分辨不出 11.5 与 12 的差别，但它意味着任何一次"整体调大一号"的改动都要在 145 个点上手工判断该不该动。原生层同样存在这个问题但程度轻：辅助窗口用 18/14/12/11/10（`QuickSearchWindow.cs:50`–`:163`），新建分类对话框全部平铺 12（`CategoryDialog.cs:48`–`:98`），桌面主界面用 13/12.5/11/10.5（`PanelWindow.cs:252`、`265`、`318`）——同一产品三套比例。

> **2026-09-20 部分推翻（`24a3e24`）**：面板层已不再是一套固定字面量 —— 原生面板的标题 14 / 计数 11 / 工具图标 15 / 重命名输入 13 / 菜单项 13 / 子菜单 12.5 均改为 `Dip(基础值)`，随 uiScale（80–130）缩放；文件名标签默认 10 → **12**、范围 [8,14] → **[8,18]**（用户已存的自定义值原样保留）。但**本节的根问题仍然成立**：这些值仍**没有**走 CSS 令牌体系（原生层不存在令牌），设置页 `styles.css` 的 145 处 `font-size` 字面量也未被本次提交触及。字号基准表见 [`../design-system/pivkey-organizer/UI-SCALING.md`](../design-system/pivkey-organizer/UI-SCALING.md) §三。

### 8. 原生菜单三套数值

| surface | 行高 | 深色底 | 深色描边 | 分隔线 |
|---|---|---|---|---|
| 面板右键菜单 | `MinHeight 32`（`PanelWindow.cs:1028`） | `ARGB(246,38,38,38)`（`:842`） | `40`（`:847`） | `22`（`:1126`） |
| 便签右键菜单 | `MinHeight 30`（`NoteWindow.cs:1047`，注释却写着"1:1 对齐 PanelWindow"`:956`） | 同面板（`:982`） | 同面板 | 同面板 |
| 托盘菜单 | `36`（`PivkeyHost.cs:492`） | `ARGB(246,44,44,44)`（`:877`） | `34`（`:889`） | 20（`:893`） |

托盘菜单是唯一有独立 `PivkeyMenuPalette` 类（`PivkeyHost.cs:873-895`）的表面，但它的 `accentValue` 参数从未被读取（`:873`），宿主却会在强调色变化时重建它（`:614-630`）——即托盘菜单是全应用唯一收不到用户强调色的表面。同理，托盘图标是静态 `.ico`（`:477-486`），无法跟随主题。

### 9. 面板与便签的逐项漂移

| 项 | 面板 | 便签 | 建议 |
|---|---|---|---|
| pin 按钮 idle 色 | `text2`（`PanelWindow.cs:2908`） | `#5A5A5A`/`#A5A5A5`（`NoteWindow.cs:882`） | 用 `text2` |
| pin 按钮变暗 | `Opacity 0.45`（`:1406`） | 无 | 补 `0.45` |
| 深色计数徽标文字 | `#FFB4B9`（`:2907`） | `#FFB4B9`（`:879`） | 改为 `themeAccent`（浅色分支已这么做） |
| 搜索/新增输入条 | 近不透明中性 `ARGB(240,251,252,253)`，字 11px（`:1825`、`:318`） | 半透明强调色 `ARGB(18,accent)`，字 11.5px（`:888`、`:411`） | 二者取一 |
| hover 强度 | 条目 `30`/`24`（`:2010`） | 待办行 `14`/`18`（`:676`） | 行类统一 30/24 |
| 圆角 Region | 每次改尺寸调 `CreateRoundRectRgn`（`:3142-3159`） | 无 `SetWindowRgn` | 对齐其一 |
| 最小尺寸 | 230×150（`:182-183`） | 声明 260（`:159`）但 resize 钳到 230（`:1275`） | 统一 230×150 |

另有一处"最后一次写入胜出"的隐性冲突：pin 按钮的 `Foreground` 在 `ApplyTheme`（`:2908`，用 `themeAccent`+`text2`）与 `UpdateBounds`（`:1407`，用分类色 `accent`+`#565C65`）两处定义，宿主的调用顺序是 `UpdateContent` → `UpdateBounds`，所以实际生效的是 `UpdateBounds` 的那一套，`ApplyTheme` 的意图被静默覆盖。

### 10. 遗留兜底色与中性色分裂

> **2026-09-20 状态变更**：本节的 `UiScale` 条目已修复；其余条目（兜底暖棕 `#8C7350` / 暖粉 `#e98687`、深色表面 `(34,33,30)` vs `(31,31,31)`、菜单勾选色硬编码）在本次提交中**未触动**，仍然有效。

- 暖棕 `#8C7350` 仍作为兜底强调色留在 `PanelWindow.cs:100`、`:101`、`:3497`、`CategoryDialog.cs:193`，另一处兜底是暖粉 `#e98687`（`PanelWindow.cs:1460-1461`），而应用默认强调色是 `#3478f6`（`Manager.cs:335`）——解析失败时会画出暖棕/暖粉对蓝色 UI。
- 深色表面有两个值：`(34,33,30)`（`Manager.cs:1870`，被面板与便签消费）与 `(31,31,31)`（`PanelWindow.cs:3042`、`:2949`、`NoteWindow.cs:861`），而注释声明中性色是 `#1F1F1F`（`:2918`）。折叠胶囊与展开面板因此落在不同的深色上。
- 菜单勾选色硬编码 `#3478f6`（`PanelWindow.cs:867`），在已支持强调色的界面里唯一不跟随。
- `UiScale`（80–130，`Manager.cs:190`、校验 `:1105`、写入 `:897`）没有进入 `PanelSyncData`（`PivkeyHost.cs:45-80`），缩放只作用于 Web 层，原生窗口不跟随。

> **2026-09-20 已修复（`24a3e24`）**：`PanelSyncData` 已补 `uiScale` 字段， `Manager.BuildPanels()` 已赋值、`BuildPanelKey()` 已把 `uiScale` 计入签名，前端 payload / `buildPanelSyncKey` / `usePanelSync` 的 effect 依赖也补齐。原生 `PanelWindow` 新增 `Dip(x) = x * uiScale / 100`（只乘 `uiScale`，**不乘** 系统 DPI —— WPF 已按 DPI 缩放 DIP），字号 / 标题条高 / 胶囊边长 / 最小宽高全部走它。
> 注意本条只涉及**逻辑尺寸**；图标位图另有一套 `ResolveIconPixelSize(logical, uiScale, dpiScale)`（向上取 16 倍数、夹到 [48,256]）。验收数据（1920×1080 @150%）：uiScale 100 → 折叠标题条 30px；uiScale 130 → 39px（30×1.3）；胶囊 56 → 73px。
> 本节其余条目（兜底暖棕/暖粉、深色表面双值、菜单勾选色）**不在本次修复范围**，仍待处理。完整契约见 [`../design-system/pivkey-organizer/UI-SCALING.md`](../design-system/pivkey-organizer/UI-SCALING.md)。

---

## 五、P2：可访问性与资产

### 11. 对比度不足（WCAG 2.x，实测）

| 组合 | 比值 | 判定 |
|---|---|---|
| `--chi-subtle #a28d88` on `--chi-paper #fff9f0` | **2.99** | 失败（全文件最低） |
| `--st-muted #8e8e93` on `#f8f9fa` | **3.09** | 失败 |
| `--st-muted #8e8e93` on `--st-card #ffffff` | **3.26** | 失败 |
| 强调色 `#3478f6` on `#f8f9fa` | **3.86** | 失败 |
| 强调色 `#3478f6` on `#ffffff` | **4.07** | 正文失败（按钮上配白字 4.07 也偏紧） |
| 强调色 `#3478f6` on `--st-card #2b2825`（深色） | **3.60** | 失败 |
| Toast 动作色 `#88aaf9` on `#4b4b4d` | **3.80** | 失败 |
| （对照）`--chi-muted #786562` on `#fff9f0` | 5.23 | 通过 |

根因集中在一个令牌：`#8e8e93` 是 iOS 的三级标签色，设计给 ≥17px 使用，本项目把它用在 10–11.5px 的 `.switch-row small`、`.settings-note`、`.settings-section__heading small`、`.preview-row small` 上。改一个令牌即可同时修复十几处。

### 12. 焦点与光标

6 处 `outline: none`（`357`、`458`、`800`、`1077`、`1271`、`4214`）都有替代焦点样式，没有"孤儿"。但 `4214` 用 24% 透明度的强调色环重新压掉了 `:focus-visible` 的实心轮廓（`3460`），使该输入的焦点提示明显偏弱。

缺失 `cursor: pointer` 的都是"整块可点的 label"：`.range-row`（433）、`.settings-select`（1050）、`.item-layout-editor label`（1095）、`.rgb-editor label`（791）、`.field`（348）。同样模式的 `.switch-row`（391）有，所以是同类交互不一致而非遗漏。

### 13. 减少动效只改了时长，没关掉位移

`styles.css:1536-1541` 的全局兜底是有效的（`*` + `!important`），但它只把 duration 压到 0.01ms，**transform 仍然生效**：`.button:active{scale(.97)}`（325）、`.icon-button:active{scale(.9)}`（299）、滑杆滑块 hover/active 的 `scale(1.04/1.1)`（477-478）、`.color-field button:hover{scale(1.1)}`（376）、全局 `:active{transform:translateY(1px)}`（2982、3986-3994）——开启减少动效后，点击仍然是瞬移式的 1px 跳变。此外 `will-change`（246、266）未在减少动效下复位，`4026` 的手写选择器清单已经落后于它要覆盖的新层（`.rule-row` 4239、`.rule-toggle` 4320、`.advanced-action-btn` 4351、`.workspace-preset-card` 4515、`.surface-preset-btn` 2269、`.classification-rules > div` 4752 都不在清单内，目前靠 1536 兜住）。

### 14. 资产重量与角色图标使用方式不匹配

6 张角色 PNG 均为 **1254×1254、约 1 MB**（共 6.2 MB，全部进入 `dist/assets/`），而实际显示尺寸是设置页 36px（`styles.css:3678`）、图标选择器 46px（`:2654`）。以 46px 显示 1254px 的图，像素量约为所需的 740 倍；每个面板条目渲染分类图标时都会解码同量级位图（`characterIcons.ts` 的 8 个默认分类映射）。另外 `src/assets/fonts/` 里 **79 MB** 的 Maple Mono TTF 已不被 Web 构建引用（`dist/assets/` 无 ttf），原生 `FontResources.CreateTrayFont` 也已改为硬编码 `"Microsoft YaHei UI"`（`FontResources.cs:67`），它构建的 `TrayFontCollection` / `TrayFontFamily`（`:13-14`、`:39-61`）成为死代码——而 `FindFont` 仍会在 `fonts/` 或 `web/assets/` 下尝试查找这 4 个文件。

### 15. 文档与实现漂移

- `design-review/README.md` 写"保留 Maple Mono-NF-CN"，`styles.css:14` 的注释写"移除 82.5MB 的外部 TTF 字体，直接使用 Windows 内置 Fluent 字体"，`native/FontResources.cs:12` 又写 `"Microsoft YaHei UI, Segoe UI Variable Text, Segoe UI"`，而 Web 侧 `styles.css:30` 是 `"Segoe UI Variable Text", "Segoe UI", "Microsoft YaHei UI", …`——**字体族优先级在两个运行时里是相反的**，同一句中文/英文混排在设置页与原生窗口会用不同字面。
- `chiikawa-review.md` 的 `--review-coral #EF8D86` / `--review-muted #8D7770` 与实现的 `--chi-coral #e98687` / `--chi-muted #786562` 不一致。
- 死规则：`.settings-brand-card`（1771-1786 等 3 处）、`.rule-editor__pattern`（1278）、`.rule-row__name`（1304）、`.rule-row__pattern`（1305）、`.settings-note--success`（746）在 `src/**/*.tsx` 中无消费者；`.settings-section > *::before` 的 hairline 系统（698-720、1644-1661）已被 P2 的 `display:none`（2013）整体关闭，但因基础规则未声明 `display`，它派生出的 `.rule-row + .rule-row::before`（1292-1301）、`.workspace-row + .workspace-row::before`（1293）、`.classification-rules > div + div::before`（1243-1251）仍然生效——P7 已把这些行改成描边圆角卡片（4244-4246、4604-4606、4752-4762），于是每张卡片内部多出一条偏移 14–16px 的孤立 hairline。

---

## 六、已经对齐的部分（不要在整改中破坏）

- **令牌结构本身**：`tokens.css` 的三段式命名（色/间距/圆角/时长/缓动）和 `styles.css:73-125` 的语义令牌分层方向正确，问题只是引用率不足。
- **交互基座**：`Modal.tsx` 的焦点陷阱、Esc 关闭、拖动取消、焦点归还完整；`Toast` 有 `role="status"`；开关/滑杆/分段的键盘可达性在 CSS 里有 `:focus-visible` 恢复。
- **原生主题管线**：面板与便签都正确接收 dark/accent/material，亚克力在 Win11 上的自动停用（`MaxAcrylicBuild = 21999`）有明确注释与降级链。
- **图标体系**：`@phosphor-icons/react` 覆盖了所有功能性图标，尺寸用法（13/14/15/16/17/18）整体一致，emoji 只出现在便签贴士一处。
- **构建与测试**：36 个单测通过，`dist/` 产物是当天的。

> **2026-09-20 更新**：原生面板的 Phosphor 尺寸已不再硬编码 —— 标题栏按钮图标统一为 `Dip(15)`（随 uiScale 缩放）；图标位图也从固定 96px 画布改为 `ResolveIconPixelSize` 的像素档位。测试规模为 6 个文件 / 45 个用例。详见 [`../design-system/pivkey-organizer/UI-SCALING.md`](../design-system/pivkey-organizer/UI-SCALING.md)。

---

## 七、整改建议（按投入产出排序）

### 第一步：先定"最终值"（1 天内，不改代码）

产出一份 `design-system/ACTIVE.md`，只写当前生效的真相，并删除或改名会误导人的 `MASTER.md`：

1. 明确**一个强调色默认值**（建议保留 `#3478f6`，因为它同时是 `tokens.css`、`Manager.cs:335`、`CategoryModal.tsx:10` 的既有默认），把珊瑚 `#e98687` 降级为配色方案之一而不是第二套主题；
2. 明确**一个中性表面组合**（`#ffffff` / `#2b2825` 或 `#F3F3F3` / `#1F1F1F`，二选一，全栈统一）；
3. 定义**字号级差**（建议 5 档：11 / 12 / 13 / 16 / 20）与**圆角级差**（建议 4 档：6 / 8 / 12 / 16），把现有 17 个字号与 24 个圆角各自归位；
4. 明确**一行菜单高度的唯一值**（建议 32，取 Win11 规范，托盘 36 与便签 30 向它靠）。

### 第二步：止血（2–3 天）

1. **把便签页并入设计系统**：删除 `SettingsModal.tsx:517-658` 的 23 处内联样式，改为 `.settings-section` / `.settings-command` / `.preview-row` 等既有类；emoji 换成 Phosphor 图标（`Lightbulb` / `Lightning` / `PushPin`）；`#d97706` 换成令牌。这是当前最集中的风格断裂点。
2. **给三个辅助窗口接主题**：`CategoryDialog`、`QuickSearchWindow`、`SettingsWindow` 接收 dark/accent 参数（管线照抄 `PivkeyHost.cs:1666` 的 `ApplyThemeVisuals`），设置窗口补 `#1F1F1F` 深色分支。
3. **修对比度**：把 `--st-muted` / `--chi-subtle` 在 11px 场景下的取值降到 `#6b6b70` 一级（比值可从 3.26 提到约 5.0），或将文字升到 12px。一个令牌可修复十几处。

   > **2026-09-20 部分先行**：`24a3e24` 已把**原生面板**的文件名标签默认字号从 10 提到 12（范围 8–18，用户自定义值保留），并提供设置页「清晰 / 标准 / 紧凑」三档预设（清晰档 = 标签 14 / 图标 64 / 项目 92 / 间距 10）。但这只是面板层的字号提升，**本节针对设置页 `--st-muted` 的对比度修复仍未做**。字体级差的唯一权威说明见 [`../design-system/pivkey-organizer/UI-SCALING.md`](../design-system/pivkey-organizer/UI-SCALING.md)。
4. **补 `cursor: pointer`** 到 5 个 label 类；把 `styles.css:4213` 的 24% 焦点环改为 ≥40%。

### 第三步：结构性收敛（1–2 周）

1. **删减 CSS 叠加层**：把 P1–P4 中被 P5/P7 完全覆盖的规则整段删除（`.settings-section` 的 4 次重写、`.switch-row` 的 4 次、`--settings-rail` / `--settings-content-max` 的失效定义、7 个无消费者选择器）。目标是让每个组件的最终值只出现一次。
2. **拆文件**：按层拆成 `tokens.css`（已存在）/ `base.css` / `components.css` / `settings.css`，用构建顺序代替"往文件末尾追加"。
3. **消除 `!important`**：48 处中 25 处可直接删除；`4055` 与 `46xx` 系列需要用真实的层叠结构（`:where()` 降权或显式复合选择器）替代。
4. **原生菜单抽公共工厂**：把 `PanelWindow.cs:840-868` 的菜单色板、`:1028` 的行高、`:3215-3250` 的动画提升为共享类，让便签菜单与托盘菜单引用同一份；同时修掉 pin 按钮的双重定义（`:1407` vs `:2908`）与深色徽标 `#FFB4B9`。
5. **资产优化**：角色 PNG 导出 256px 版本（视觉无损，体积可降到约 1/20），或改成 `srcset`；从仓库删除 79 MB 死字体与失效的 `TrayFontCollection` 代码路径。
6. **补一条视觉回归**：在 `npm test` 旁加一个截图对比任务（设置页 + 面板 + 便签 × 浅色/深色），把"某个值被哪一层覆盖"变成可见的 CI 输出，防止再出现第 6 次叠加。

---

## 附录：方法与证据

- CSS 统计来自对 `src/styles.css` 的 grep/awk 实测：`font-size` 145 处（无一处 `var()`）、`border-radius` 159 处（27 处走 `--radius-*`）、hex 色值 175 个 / 71 种、`#3478f6` 64 处且 64/64 为 `var()` 兜底、`!important` 48 处、`var()` 1141 次。
- 分层与生效值判定：注释剥除后解析 600 个选择器 / 3390 条规则 / 3527 条声明，按 (specificity, order) 求每个属性的胜出值。
- 对比度按 WCAG 2.x 相对亮度公式计算（`L = 0.2126R + 0.7152G + 0.0722B`，sRGB 线性化，`R = (L_hi+0.05)/(L_lo+0.05)`）。
- 原生层证据全部来自 `native/*.cs` 的 grep 与定点阅读；`Manager.cs` 经确认不含任何绘制代码（无 `System.Drawing` / `Graphics` / `NotifyIcon` 引用），托盘渲染实际位于 `PivkeyHost.cs`。
- 资产尺寸由 PNG 头（偏移 16/20）直接读取。
- 验证命令：`npm test` → 6 个文件 / 36 个用例全部通过。

> **2026-09-20 更新**：`npm test` → 6 个文件 / **45** 个用例全部通过（新增 9 个，覆盖 `uiScale` 同步键敏感性、`uiScale` 默认值 100 与 80–130 夹取、`labelSize` 默认 12 与旧值不被覆盖、新区间 [8,18] 夹取、窄视口右停靠无负 x）。`npm run build`（tsc + vite）与 `scripts/build-windows.ps1`（csc /langversion:5）均通过。
