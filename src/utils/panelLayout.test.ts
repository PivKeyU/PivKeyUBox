import { describe, expect, it } from 'vitest';
import type { LayoutPreset, ZoneSizes } from '../state/layout';
import {
  alignPanelLayout,
  createPresetLayout,
  panelLayoutGap,
} from './panelLayout';

const viewport = { width: 1920, height: 1080, margin: 24, top: 24, bottom: 24, gap: 16 };
const ids = ['one', 'two', 'three', 'four', 'five', 'six', 'seven'];

type PanelRect = { id: string; x: number; y: number; width: number; height: number };

function rects(positions: Record<string, { x: number; y: number }>, sizes: Record<string, { width: number; height: number }>) {
  return Object.keys(positions).map((id) => ({ id, ...positions[id], ...sizes[id] }));
}

function overlaps(a: PanelRect, b: PanelRect, gap = 0) {
  return a.x < b.x + b.width + gap
    && a.x + a.width + gap > b.x
    && a.y < b.y + b.height + gap
    && a.y + a.height + gap > b.y;
}

function expectNoOverlap(items: PanelRect[], gap = 0) {
  for (let index = 0; index < items.length; index += 1) {
    for (let other = index + 1; other < items.length; other += 1) {
      expect(overlaps(items[index], items[other], gap)).toBe(false);
    }
  }
}

describe('panel layout geometry', () => {
  it('uses edge-to-edge panel docking by default', () => {
    expect(panelLayoutGap).toBe(0);
    const layout = alignPanelLayout(
      ['one', 'two'],
      { one: { width: 292, height: 200 }, two: { width: 300, height: 200 } },
      'left',
      { width: 1000, height: 700, margin: 24, top: 24, bottom: 24 },
    );
    expect(layout.positions.two.x).toBe(layout.positions.one.x + layout.sizes.one.width);
  });

  it.each(['balanced', 'grid', 'columns', 'corners', 'right-dock'] as LayoutPreset[])('keeps %s preset inside the work area', (preset) => {
    const layout = createPresetLayout(ids, preset, viewport);
    const items = rects(layout.positions, layout.sizes);
    expectNoOverlap(items);
    items.forEach((item) => {
      expect(item.x).toBeGreaterThanOrEqual(viewport.margin!);
      expect(item.y).toBeGreaterThanOrEqual(viewport.top!);
      expect(item.x + item.width).toBeLessThanOrEqual(viewport.width - viewport.margin!);
      expect(item.y + item.height).toBeLessThanOrEqual(viewport.height - viewport.bottom!);
    });
  });

  it('falls back to a stable grid when edge presets cannot fit', () => {
    const layout = createPresetLayout(ids, 'balanced', { width: 800, height: 560, margin: 24, top: 24, bottom: 24, gap: 16 });
    expectNoOverlap(rects(layout.positions, layout.sizes));
  });

  it('aligns each row without changing the visual order', () => {
    const sizes: ZoneSizes = {
      one: { width: 292, height: 200 },
      two: { width: 400, height: 180 },
      three: { width: 250, height: 260 },
      four: { width: 300, height: 220 },
    };
    const layout = alignPanelLayout(['one', 'two', 'three', 'four'], sizes, 'center', { width: 1000, height: 800, margin: 24, top: 24, bottom: 24, gap: 16 });
    const items = rects(layout.positions, layout.sizes);
    expectNoOverlap(items);
    expect(layout.positions.one.x).toBeLessThan(layout.positions.three.x);
    expect(layout.positions.one.y).toBe(layout.positions.two.y);
    expect(layout.positions.three.y).toBeGreaterThan(layout.positions.one.y);
    expect(layout.positions.three.x).toBeLessThan(layout.positions.four.x);
  });

  // 7 个面板在 1080p 高度下单列放不下，用更高视口确保全部落在同一列贴右缘
  it('stacks the right-dock preset along the right edge, bottom-aligned', () => {
    const tallViewport = { width: 1920, height: 1400, margin: 24, top: 24, bottom: 24, gap: 16 };
    const layout = createPresetLayout(ids, 'right-dock', tallViewport);
    const items = rects(layout.positions, layout.sizes);
    items.forEach((item) => {
      expect(item.x + item.width).toBe(tallViewport.width - tallViewport.margin);
    });
    expect(Math.max(...items.map((item) => item.y + item.height))).toBe(tallViewport.height - tallViewport.bottom);
    expectNoOverlap(items);
  });

  // 统一行高 + 整体垂直居中：可用高度充足时分组居中，不悬在顶部
  it('centers the aligned group vertically when space allows', () => {
    const layout = alignPanelLayout(
      ['one', 'two'],
      { one: { width: 292, height: 200 }, two: { width: 300, height: 200 } },
      'center',
      { width: 1000, height: 800, margin: 24, top: 24, bottom: 24, gap: 16 },
    );
    expect(layout.positions.one.y).toBeGreaterThan(24);
    expect(layout.positions.two.y).toBe(layout.positions.one.y);
  });
});
