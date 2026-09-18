export function hashPanelValue(value: unknown): string {
  if (typeof value !== 'string' || !value) return '0';
  let hash = 2166136261;
  for (let index = 0; index < value.length; index += 1) {
    hash ^= value.charCodeAt(index);
    hash = Math.imul(hash, 16777619);
  }
  return (hash >>> 0).toString(36);
}

export function buildPanelSyncKey(panels: Array<Record<string, unknown>>): string {
  return panels.map((panel) => {
    const items = (panel.items as Array<Record<string, unknown>> | undefined) ?? [];
    const itemKey = items.map((item) => [
      item.id,
      item.name,
      item.modifiedAt,
      item.size,
      hashPanelValue(item.iconUrl),
    ].join(':')).join(',');
    return [
      panel.id, panel.name, panel.color, panel.themeAccent, panel.headerSurface, panel.glassOpacity, panel.theme, panel.capsuleMode,
      panel.compact, panel.viewMode, panel.sortMode, panel.itemSize, panel.iconSize, panel.itemGap, panel.itemAlignment,
      panel.itemColumns, panel.showLabels, panel.labelSize, panel.x, panel.y, panel.width, panel.height, panel.pinned, panel.collapsed,
      itemKey,
    ].join('|');
  }).join('//');
}
