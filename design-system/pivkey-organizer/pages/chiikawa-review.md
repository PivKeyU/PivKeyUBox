# 吉伊系 UI Review Override

本页是 Pivkey Organizer 的视觉审阅变体，只有在用户确认后才接入正式页面。

## Direction

- 视觉语言：奶油纸张、圆润小生物、简洁深棕线稿、低饱和粉彩物件。
- 角色定位：原创吉伊气质 mascot，用于帮助识别语义，不承担复杂信息表达。
- 形状：10–28px 圆角层级；按钮不使用跳动或大幅缩放造成布局位移。
- 动效：默认 150–220ms 的颜色/透明度变化；尊重 `prefers-reduced-motion`。

## Semantic Colors

| Token | Value | Use |
| --- | --- | --- |
| `--review-paper` | `#FFF9F0` | 应用背景 |
| `--review-ink` | `#3D302C` | 主文字、托盘标题 |
| `--review-muted` | `#8D7770` | 辅助文字 |
| `--review-coral` | `#EF8D86` | 选择、强调、危险操作 |
| `--review-mint` | `#9ECFC0` | 开启、成功、同步 |
| `--review-blue` | `#A9CBE4` | 图片、媒体、信息 |
| `--review-yellow` | `#F4C968` | 文件夹、提醒、收藏 |
| `--review-lavender` | `#C5B7DF` | 视频、开发、实验 |

## Icon contract

- 每个图标同时具备 `aria-label` 或与可见文字组合；纯装饰图标使用 `aria-hidden="true"`。
- 保留 Phosphor 作为未覆盖语义的 fallback，正式接入时不混用 emoji。
- 资源必须以独立文件保存，支持 20px、28px、38px、58px 四档显示。
- 角色细节不能影响文件类型识别，必要时让语义物件占图标主体面积。
