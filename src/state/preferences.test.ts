import { describe, expect, it } from 'vitest';
import { defaultItemLayout, normalizeItemLayout, normalizeItemLayouts } from './preferences';

describe('item layout normalization', () => {
  it('clamps malformed values to the same lightweight UI bounds as the native panel', () => {
    expect(normalizeItemLayout({ itemSize: 999, iconSize: -2, gap: 80, columns: 99, labelSize: 1, alignment: 'bad' })).toEqual({
      itemSize: 112,
      iconSize: 24,
      gap: 24,
      alignment: 'left',
      columns: 8,
      showLabels: true,
      labelSize: 8,
    });
  });

  it('ignores invalid layout records and caps the number of stored panels', () => {
    const layouts = normalizeItemLayouts({ good: defaultItemLayout, bad: null, '': defaultItemLayout });
    expect(Object.keys(layouts)).toEqual(['good']);
    expect(normalizeItemLayouts(null)).toEqual({});
  });
});
