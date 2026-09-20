import type { FolderAlignment, LayoutPreset, ZonePositions, ZoneSize, ZoneSizes } from '../state/layout';

export interface PanelViewport {
  width: number;
  height: number;
  margin?: number;
  top?: number;
  bottom?: number;
  gap?: number;
}

interface NormalizedViewport {
  width: number;
  height: number;
  margin: number;
  top: number;
  bottom: number;
  gap: number;
  availableWidth: number;
  availableHeight: number;
}

interface GridShape {
  columns: number;
  rows: number;
  panelWidth: number;
  panelHeight: number;
}

export const panelMinWidth = 230;
export const panelMinHeight = 150;
export const panelLayoutGap = 0;

function clamp(value: number, minimum: number, maximum: number) {
  return Math.max(minimum, Math.min(maximum, value));
}

function normalizeViewport(viewport: PanelViewport): NormalizedViewport {
  const width = Math.max(1, viewport.width);
  const height = Math.max(1, viewport.height);
  const margin = clamp(viewport.margin ?? 24, 10, Math.max(10, Math.floor(width / 4)));
  const top = clamp(viewport.top ?? margin, 10, Math.max(10, height - 10));
  const bottom = clamp(viewport.bottom ?? margin, 10, Math.max(10, height - top));
  const gap = clamp(viewport.gap ?? panelLayoutGap, 0, 28);
  return {
    width,
    height,
    margin,
    top,
    bottom,
    gap,
    availableWidth: Math.max(panelMinWidth, width - margin * 2),
    availableHeight: Math.max(panelMinHeight, height - top - bottom),
  };
}

function chooseGrid(count: number, viewport: NormalizedViewport, maxPanelWidth = 320, maxPanelHeight = 230, maxColumns = Number.POSITIVE_INFINITY): GridShape {
  const capacityColumns = Math.max(1, Math.floor((viewport.availableWidth + viewport.gap) / (panelMinWidth + viewport.gap)));
  const capacityRows = Math.max(1, Math.floor((viewport.availableHeight + viewport.gap) / (panelMinHeight + viewport.gap)));
  let best: GridShape | null = null;
  let bestScore = Number.POSITIVE_INFINITY;

  for (let columns = 1; columns <= Math.min(count, capacityColumns, maxColumns); columns += 1) {
    const rows = Math.ceil(count / columns);
    if (rows > capacityRows) continue;
    const cellWidth = Math.floor((viewport.availableWidth - viewport.gap * (columns - 1)) / columns);
    const cellHeight = Math.floor((viewport.availableHeight - viewport.gap * (rows - 1)) / rows);
    const panelWidth = clamp(cellWidth, panelMinWidth, maxPanelWidth);
    const panelHeight = clamp(cellHeight, panelMinHeight, maxPanelHeight);
    const ratioPenalty = Math.abs(panelWidth / panelHeight - 1.36);
    const sizePenalty = Math.abs(panelWidth - 292) / 292 + Math.abs(panelHeight - 210) / 210;
    const emptyPenalty = (columns * rows - count) * .08;
    const score = ratioPenalty * .75 + sizePenalty * .22 + emptyPenalty;
    if (score < bestScore) {
      bestScore = score;
      best = { columns, rows, panelWidth, panelHeight };
    }
  }

  if (best) return best;
  const columns = Math.max(1, Math.min(count, capacityColumns));
  const rows = Math.ceil(count / columns);
  return { columns, rows, panelWidth: panelMinWidth, panelHeight: panelMinHeight };
}

function setPanel(result: { positions: ZonePositions; sizes: ZoneSizes }, id: string, x: number, y: number, width: number, height: number) {
  result.positions[id] = { x: Math.round(x), y: Math.round(y) };
  result.sizes[id] = { width: Math.round(width), height: Math.round(height) };
}

function placeCenteredRows(ids: string[], viewport: NormalizedViewport, shape: GridShape) {
  const result = { positions: {} as ZonePositions, sizes: {} as ZoneSizes };
  const groupHeight = shape.rows * shape.panelHeight + Math.max(0, shape.rows - 1) * viewport.gap;
  const startY = viewport.top + Math.max(0, Math.floor((viewport.availableHeight - groupHeight) / 2));
  ids.forEach((id, index) => {
    const row = Math.floor(index / shape.columns);
    const column = index % shape.columns;
    const rowCount = Math.min(shape.columns, ids.length - row * shape.columns);
    const rowWidth = rowCount * shape.panelWidth + Math.max(0, rowCount - 1) * viewport.gap;
    const startX = viewport.margin + Math.max(0, Math.floor((viewport.availableWidth - rowWidth) / 2));
    setPanel(result, id, startX + column * (shape.panelWidth + viewport.gap), startY + row * (shape.panelHeight + viewport.gap), shape.panelWidth, shape.panelHeight);
  });
  return result;
}

function createBalancedLayout(ids: string[], viewport: NormalizedViewport) {
  const result = { positions: {} as ZonePositions, sizes: {} as ZoneSizes };
  const leftIds = ids.filter((_, index) => index % 2 === 0);
  const rightIds = ids.filter((_, index) => index % 2 === 1);
  const sideCount = Math.max(leftIds.length, rightIds.length);
  const rowCapacity = Math.max(1, Math.floor((viewport.availableHeight + viewport.gap) / (panelMinHeight + viewport.gap)));
  const rows = Math.min(sideCount, rowCapacity);
  const columnsPerSide = Math.max(1, Math.ceil(sideCount / rows));
  const minimumCenter = Math.max(72, Math.min(420, Math.round(viewport.availableWidth * .28)));
  const minimumRequiredWidth = panelMinWidth * columnsPerSide * 2 + viewport.gap * (Math.max(0, columnsPerSide - 1) * 2 + 1);
  if (minimumRequiredWidth > viewport.availableWidth) return placeCenteredRows(ids, viewport, chooseGrid(ids.length, viewport));
  const widthForPanels = Math.max(panelMinWidth * 2 * columnsPerSide, viewport.availableWidth - minimumCenter - viewport.gap * 2 * Math.max(0, columnsPerSide - 1));
  const panelWidth = clamp(Math.floor(widthForPanels / (columnsPerSide * 2)), panelMinWidth, 310);
  const panelHeight = clamp(Math.floor((viewport.availableHeight - viewport.gap * Math.max(0, rows - 1)) / rows), panelMinHeight, 225);

  const placeSide = (sideIds: string[], right: boolean) => {
    const sideRows = Math.min(rows, Math.max(1, sideIds.length));
    const stackHeight = sideRows * panelHeight + Math.max(0, sideRows - 1) * viewport.gap;
    const startY = viewport.top + Math.max(0, Math.floor((viewport.availableHeight - stackHeight) / 2));
    sideIds.forEach((id, index) => {
      const column = Math.floor(index / rows);
      const row = index % rows;
      const x = right
        ? viewport.width - viewport.margin - panelWidth - column * (panelWidth + viewport.gap)
        : viewport.margin + column * (panelWidth + viewport.gap);
      setPanel(result, id, x, startY + row * (panelHeight + viewport.gap), panelWidth, panelHeight);
    });
  };

  placeSide(leftIds, false);
  placeSide(rightIds, true);
  return result;
}

function createDockLayout(ids: string[], viewport: NormalizedViewport) {
  const result = { positions: {} as ZonePositions, sizes: {} as ZoneSizes };
  // 单列容量：按最小面板高度估算一列能放几个
  const columnCapacity = Math.max(1, Math.floor((viewport.availableHeight + viewport.gap) / (panelMinHeight + viewport.gap)));
  const columns = Math.max(1, Math.ceil(ids.length / columnCapacity));
  // 视口很窄时最少列宽都放不下这么多列：交给通用网格（会退回居中行布局），
  // 否则最左边几列的 x 会算成负数，把面板推出屏幕左缘。与 corners/balanced
  // 预设的兜底写法一致（原生 Manager.CreateDockLayout 同步保持此行为）。
  const minimumRequiredWidth = panelMinWidth * columns + viewport.gap * Math.max(0, columns - 1);
  if (minimumRequiredWidth > viewport.availableWidth) return placeCenteredRows(ids, viewport, chooseGrid(ids.length, viewport));
  const panelWidth = clamp(Math.floor((Math.min(viewport.availableWidth, columns * 304 + Math.max(0, columns - 1) * viewport.gap) - Math.max(0, columns - 1) * viewport.gap) / columns), panelMinWidth, 304);
  const panelHeight = clamp(Math.floor((viewport.availableHeight - Math.max(0, columnCapacity - 1) * viewport.gap) / columnCapacity), panelMinHeight, 220);
  ids.forEach((id, index) => {
    const column = Math.floor(index / columnCapacity);   // 先填满最右列
    const row = index % columnCapacity;
    const x = viewport.width - viewport.margin - panelWidth - column * (panelWidth + viewport.gap);
    const y = viewport.height - viewport.bottom - panelHeight - row * (panelHeight + viewport.gap); // 从底部向上堆叠
    setPanel(result, id, x, y, panelWidth, panelHeight);
  });
  return result;
}

function createColumnLayout(ids: string[], viewport: NormalizedViewport) {
  let shape = chooseGrid(ids.length, viewport, 320, 240, 3);
  const rowCapacity = Math.max(1, Math.floor((viewport.availableHeight + viewport.gap) / (panelMinHeight + viewport.gap)));
  if (shape.rows > rowCapacity) shape = chooseGrid(ids.length, viewport, 320, 240);
  const result = { positions: {} as ZonePositions, sizes: {} as ZoneSizes };
  const groupWidth = shape.columns * shape.panelWidth + Math.max(0, shape.columns - 1) * viewport.gap;
  const groupHeight = shape.rows * shape.panelHeight + Math.max(0, shape.rows - 1) * viewport.gap;
  const startX = viewport.margin + Math.max(0, Math.floor((viewport.availableWidth - groupWidth) / 2));
  const startY = viewport.top + Math.max(0, Math.floor((viewport.availableHeight - groupHeight) / 2));
  ids.forEach((id, index) => {
    const column = Math.floor(index / shape.rows);
    const row = index % shape.rows;
    setPanel(result, id, startX + column * (shape.panelWidth + viewport.gap), startY + row * (shape.panelHeight + viewport.gap), shape.panelWidth, shape.panelHeight);
  });
  return result;
}

function createCornerLayout(ids: string[], viewport: NormalizedViewport) {
  const result = { positions: {} as ZonePositions, sizes: {} as ZoneSizes };
  const layers = Math.max(1, Math.ceil(ids.length / 4));
  const minimumRequiredWidth = panelMinWidth * layers * 2 + viewport.gap * Math.max(1, layers * 2 - 1);
  if (minimumRequiredWidth > viewport.availableWidth) return placeCenteredRows(ids, viewport, chooseGrid(ids.length, viewport));
  const panelWidth = clamp(Math.floor((viewport.availableWidth - Math.max(1, layers * 2 - 1) * viewport.gap) / (layers * 2)), panelMinWidth, 300);
  const panelHeight = clamp(Math.floor((viewport.availableHeight - viewport.gap) / 2), panelMinHeight, 220);
  ids.forEach((id, index) => {
    const corner = index % 4;
    const layer = Math.floor(index / 4);
    const fromLeft = corner === 0 || corner === 2;
    const fromTop = corner < 2;
    const x = fromLeft
      ? viewport.margin + layer * (panelWidth + viewport.gap)
      : viewport.width - viewport.margin - panelWidth - layer * (panelWidth + viewport.gap);
    const y = fromTop ? viewport.top : viewport.height - viewport.bottom - panelHeight;
    setPanel(result, id, x, y, panelWidth, panelHeight);
  });
  return result;
}

export function createPresetLayout(ids: string[], preset: LayoutPreset, rawViewport: PanelViewport) {
  const viewport = normalizeViewport(rawViewport);
  if (!ids.length) return { positions: {} as ZonePositions, sizes: {} as ZoneSizes };
  if (preset === 'balanced') return createBalancedLayout(ids, viewport);
  if (preset === 'right-dock') return createDockLayout(ids, viewport);
  if (preset === 'columns') return createColumnLayout(ids, viewport);
  if (preset === 'corners') return createCornerLayout(ids, viewport);
  return placeCenteredRows(ids, viewport, chooseGrid(ids.length, viewport));
}

export function alignPanelLayout(ids: string[], currentSizes: ZoneSizes, alignment: Exclude<FolderAlignment, 'manual'>, rawViewport: PanelViewport) {
  const viewport = normalizeViewport(rawViewport);
  const result = { positions: {} as ZonePositions, sizes: {} as ZoneSizes };
  const rows: Array<Array<{ id: string; size: ZoneSize }>> = [];
  let row: Array<{ id: string; size: ZoneSize }> = [];
  let rowWidth = 0;
  let maxHeight = panelMinHeight; // 统一行高：所有面板高度的最大值

  ids.forEach((id) => {
    const current = currentSizes[id] ?? { width: 292, height: 238 };
    const size = {
      width: clamp(Math.round(current.width), panelMinWidth, viewport.availableWidth),
      height: clamp(Math.round(current.height), panelMinHeight, viewport.availableHeight),
    };
    const nextWidth = row.length ? rowWidth + viewport.gap + size.width : size.width;
    if (row.length && nextWidth > viewport.availableWidth) {
      rows.push(row);
      row = [];
      rowWidth = 0;
    }
    row.push({ id, size });
    rowWidth = rowWidth ? rowWidth + viewport.gap + size.width : size.width;
    if (size.height > maxHeight) maxHeight = size.height;
  });
  if (row.length) rows.push(row);

  // 整体垂直居中：分组高度 = 行数 × 统一行高 + 行间 gap
  const groupHeight = rows.length * maxHeight + Math.max(0, rows.length - 1) * viewport.gap;
  let y = viewport.top + Math.max(0, Math.floor((viewport.availableHeight - groupHeight) / 2));
  rows.forEach((entries) => {
    const width = entries.reduce((total, entry) => total + entry.size.width, 0) + Math.max(0, entries.length - 1) * viewport.gap;
    let x = alignment === 'left'
      ? viewport.margin
      : alignment === 'right'
        ? viewport.width - viewport.margin - width
        : Math.round((viewport.width - width) / 2);
    entries.forEach((entry) => {
      setPanel(result, entry.id, x, y, entry.size.width, entry.size.height);
      x += entry.size.width + viewport.gap;
    });
    y += maxHeight + viewport.gap;
  });
  return result;
}
