import { describe, expect, it } from 'vitest';
import { defaultItemLayout, normalizeItemLayout, normalizeItemLayouts, normalizePreferences } from './preferences';
import { defaultPreferences } from '../data/defaults';

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

  it('defaults the file name label size to 12', () => {
    expect(defaultItemLayout.labelSize).toBe(12);
    expect(normalizeItemLayout({}).labelSize).toBe(12);
    expect(normalizeItemLayout(null).labelSize).toBe(12);
    expect(normalizeItemLayout({ itemSize: 76 }).labelSize).toBe(12);
  });

  it('keeps an existing label size instead of forcing the new default', () => {
    expect(normalizeItemLayout({ labelSize: 9 }).labelSize).toBe(9);
    expect(normalizeItemLayout({ labelSize: 10 }).labelSize).toBe(10);
    expect(normalizeItemLayout({ labelSize: 11 }).labelSize).toBe(11);
  });

  it('accepts the widened label size range and clamps outside values', () => {
    expect(normalizeItemLayout({ labelSize: 8 }).labelSize).toBe(8);
    expect(normalizeItemLayout({ labelSize: 18 }).labelSize).toBe(18);
    expect(normalizeItemLayout({ labelSize: 30 }).labelSize).toBe(18);
    expect(normalizeItemLayout({ labelSize: 2 }).labelSize).toBe(8);
    expect(normalizeItemLayout({ labelSize: Number.NaN }).labelSize).toBe(12);
  });

  it('preserves a custom label size for each panel', () => {
    const layouts = normalizeItemLayouts({ docs: { labelSize: 9 }, media: { labelSize: 16 }, broken: null });
    expect(Object.keys(layouts)).toEqual(['docs', 'media']);
    expect(layouts.docs.labelSize).toBe(9);
    expect(layouts.media.labelSize).toBe(16);
  });
});

describe('ui scale normalization', () => {
  it('falls back to 100 when missing or invalid', () => {
    expect(normalizePreferences({}).uiScale).toBe(100);
    expect(normalizePreferences(null).uiScale).toBe(100);
    expect(normalizePreferences({ uiScale: Number.NaN }).uiScale).toBe(100);
  });

  it('keeps a user value inside 80-130 and clamps outside values', () => {
    expect(normalizePreferences({ uiScale: 80 }).uiScale).toBe(80);
    expect(normalizePreferences({ uiScale: 130 }).uiScale).toBe(130);
    expect(normalizePreferences({ uiScale: 140 }).uiScale).toBe(130);
    expect(normalizePreferences({ uiScale: 40 }).uiScale).toBe(80);
  });

  it('keeps the shipped default at 100', () => {
    expect(defaultPreferences.uiScale).toBe(100);
    expect(normalizePreferences(defaultPreferences).uiScale).toBe(100);
  });
});
