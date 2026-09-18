export const schemeColors = {
  white: { accent: '#3478f6', surface: [255, 255, 255] as const },
  warm: { accent: '#e98687', surface: [255, 249, 240] as const },
  ink: { accent: '#7a8fab', surface: [246, 248, 252] as const },
  forest: { accent: '#78a68d', surface: [241, 249, 244] as const },
  rose: { accent: '#d887a0', surface: [253, 242, 246] as const },
} as const;

export type ColorSchemeId = keyof typeof schemeColors;

export function colorToRgb(value: string): [number, number, number] {
  const normalized = value.replace('#', '').padEnd(6, '0').slice(0, 6);
  return [
    Number.parseInt(normalized.slice(0, 2), 16) || 0,
    Number.parseInt(normalized.slice(2, 4), 16) || 0,
    Number.parseInt(normalized.slice(4, 6), 16) || 0,
  ];
}

export function toHexColor(rgb: readonly [number, number, number] | number[]): string {
  return `#${rgb.map((value) => Math.max(0, Math.min(255, Math.round(value))).toString(16).padStart(2, '0')).join('')}`;
}

export function resolveEffectiveTheme(theme: 'light' | 'dark' | 'system'): 'light' | 'dark' {
  if (theme === 'system') {
    return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
  }
  return theme;
}

export function resolveSurfaceRgb(
  colorScheme: ColorSchemeId | 'custom' | string,
  customColor: string,
  effectiveTheme: 'light' | 'dark',
  customSurfaceColor?: string,
): [number, number, number] {
  if (effectiveTheme === 'dark') return [34, 33, 30];
  if (colorScheme === 'custom' || !(colorScheme in schemeColors)) {
    if (customSurfaceColor && /^#[0-9a-f]{6}$/i.test(customSurfaceColor)) {
      return colorToRgb(customSurfaceColor);
    }
    return colorToRgb(customColor).map((value) => Math.round(value * 0.08 + 255 * 0.92)) as [number, number, number];
  }
  return [...schemeColors[colorScheme as ColorSchemeId].surface];
}

export function resolveAccent(colorScheme: ColorSchemeId | 'custom' | string, customColor: string): string {
  if (colorScheme === 'custom' || !(colorScheme in schemeColors)) return customColor;
  return schemeColors[colorScheme as ColorSchemeId].accent;
}
