import { useMemo } from 'react';
import type { Category } from '../types';
import type { FolderAlignment, LayoutPreset } from '../state/layout';
import { alignPanelLayout, createPresetLayout } from '../utils/panelLayout';
import { characterIconSource } from '../data/characterIcons';

export type LayoutPreviewState =
  | { kind: 'preset'; value: LayoutPreset }
  | { kind: 'alignment'; value: Exclude<FolderAlignment, 'manual'> }
  | null;

interface LayoutPreviewProps {
  categories: Category[];
  state: LayoutPreviewState;
}

// 预览使用固定的逻辑屏幕尺寸；渲染时按百分比缩放，因此自适应容器宽度。
const VIEWPORT_WIDTH = 1600;
const VIEWPORT_HEIGHT = 900;
const PREVIEW_GAP = 8;

const presetNames: Record<LayoutPreset, [string, string]> = {
  balanced: ['智能平衡', '两侧收纳，保留壁纸中心'],
  grid: ['均匀网格', '末行自动居中'],
  columns: ['等宽分栏', '纵向阅读顺序'],
  corners: ['屏幕四角', '减少中心遮挡'],
  'right-dock': ['右侧停靠', '集中到屏幕右缘'],
};

const alignmentNames: Record<Exclude<FolderAlignment, 'manual'>, [string, string]> = {
  left: ['靠左排列', '从屏幕左侧开始换行'],
  center: ['居中排列', '每一行独立居中'],
  right: ['靠右排列', '从屏幕右侧开始换行'],
};

// 用分类 id 做确定性哈希生成 3-8 个装饰圆点，透明度取 .9/.6/.35 三层，模拟真实文件图标密度
function dotsForCategory(id: string): number[] {
  let hash = 0;
  for (let i = 0; i < id.length; i += 1) {
    hash = (hash * 31 + id.charCodeAt(i)) >>> 0;
  }
  const count = 3 + (hash % 6);
  const alphas = [0.9, 0.6, 0.35];
  return Array.from({ length: count }, (_, i) => alphas[(hash + i * 3) % 3]);
}

export function LayoutPreview({ categories, state }: LayoutPreviewProps) {
  const layout = useMemo(() => {
    const ids = categories.map((category) => category.id);
    const viewport = { width: VIEWPORT_WIDTH, height: VIEWPORT_HEIGHT, margin: 24, top: 24, bottom: 24, gap: PREVIEW_GAP };
    if (!state) return null;
    if (state.kind === 'alignment') {
      const sizes = Object.fromEntries(categories.map((category) => [category.id, { width: 292, height: 220 }]));
      return alignPanelLayout(ids, sizes, state.value, viewport);
    }
    return createPresetLayout(ids, state.value, viewport);
  }, [categories, state]);

  const colorById = useMemo(() => Object.fromEntries(categories.map((category) => [category.id, category.color])), [categories]);
  const nameById = useMemo(() => Object.fromEntries(categories.map((category) => [category.id, category.name])), [categories]);
  const iconById = useMemo(() => Object.fromEntries(categories.map((category) => [category.id, characterIconSource(category.icon) ?? (category.icon.startsWith('data:') ? category.icon : undefined)])), [categories]);
  const dotsById = useMemo(() => {
    const result: Record<string, number[]> = {};
    for (const category of categories) {
      result[category.id] = dotsForCategory(category.id);
    }
    return result;
  }, [categories]);

  const meta = useMemo(() => {
    if (!state) return { title: '当前为手动布局', detail: '悬停上方预设卡片即可预览，点击才会应用' };
    const [title, detail] = state.kind === 'alignment' ? alignmentNames[state.value] : presetNames[state.value];
    return { title, detail: `${categories.length} 个分区 · ${detail}` };
  }, [categories.length, state]);

  // 右侧停靠预设：屏幕右缘显示高亮条
  const isDock = state !== null && state.kind === 'preset' && state.value === 'right-dock';

  return (
    <div className="layout-preview">
      <div className={`layout-preview__screen${isDock ? ' is-dock' : ''}`} aria-hidden="true">
        {layout && Object.entries(layout.positions).map(([id, position]) => {
          const size = layout.sizes[id];
          const color = colorById[id] ?? '#e98687';
          return (
            <div
              key={id}
              className="layout-preview__panel"
              style={{
                left: `${(position.x / VIEWPORT_WIDTH) * 100}%`,
                top: `${(position.y / VIEWPORT_HEIGHT) * 100}%`,
                width: `${(size.width / VIEWPORT_WIDTH) * 100}%`,
                height: `${(size.height / VIEWPORT_HEIGHT) * 100}%`,
                backgroundColor: `${color}1f`,
                borderColor: `${color}59`,
                boxShadow: `0 1px 2px ${color}40, 0 2px 8px ${color}26`,
              }}
            >
              <div className="layout-preview__panel-header">
                {iconById[id] ? <img src={iconById[id]} alt="" aria-hidden="true" /> : <i style={{ background: color }} />}
                <span>{nameById[id] ?? ''}</span>
              </div>
              <div className="layout-preview__panel-dots">
                {(dotsById[id] ?? []).map((alpha, index) => (
                  <i key={index} style={{ background: color, opacity: alpha }} />
                ))}
              </div>
            </div>
          );
        })}
      </div>
      <div className="layout-preview__meta"><strong>{meta.title}</strong><small>{meta.detail}</small></div>
    </div>
  );
}
