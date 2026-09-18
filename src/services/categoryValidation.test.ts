import { describe, expect, it } from 'vitest';
import { defaultCategories } from '../data/defaults';
import { normalizePreferences } from '../state/preferences';
import { normalizeCategory, normalizeCategoryName } from './categoryValidation';

describe('category validation', () => {
  it('normalizes a valid category name', () => {
    expect(normalizeCategoryName('  设计源文件  ')).toBe('设计源文件');
  });

  it.each(['.', '..', '..\\Documents', '设计/源文件', 'CON', 'LPT1.txt', 'name.'])('rejects unsafe Windows category name %s', (name) => {
    expect(() => normalizeCategoryName(name)).toThrow();
  });

  it('rejects duplicate names without case sensitivity', () => {
    expect(() => normalizeCategoryName('Documents', ['documents'])).toThrow('已经存在');
  });

  it('normalizes persisted category data', () => {
    expect(normalizeCategory({
      id: 'custom',
      name: '代码',
      icon: 'code',
      color: '#112233',
      extensions: ['.TS', 'ts', 1],
    }, 'fallback')).toEqual({
      id: 'custom',
      name: '代码',
      icon: 'code',
      color: '#112233',
      extensions: ['ts'],
      acceptsFolders: false,
    });
  });
});

describe('preference validation', () => {
  it('clamps numeric values and rejects malformed categories', () => {
    const preferences = normalizePreferences({
      categories: [{ name: '..', extensions: [] }],
      glassOpacity: 999,
      organizeDelaySeconds: -1,
      uiScale: Number.NaN,
      theme: 'unexpected',
    });
    expect(preferences.categories).toHaveLength(defaultCategories.length);
    expect(preferences.glassOpacity).toBe(100);
    expect(preferences.organizeDelaySeconds).toBe(5);
    expect(preferences.uiScale).toBe(100);
    expect(preferences.theme).toBe('light');
  });

  it('deduplicates recovered category ids and names', () => {
    const preferences = normalizePreferences({
      categories: [
        { id: 'one', name: '代码', icon: 'code', color: '#112233', extensions: ['ts'] },
        { id: 'two', name: '代码', icon: 'code', color: '#112233', extensions: ['tsx'] },
        { id: 'one', name: '其他', icon: 'code', color: '#112233', extensions: ['txt'] },
      ],
    });
    expect(preferences.categories.map((category) => category.name)).toEqual(['代码']);
  });
});
