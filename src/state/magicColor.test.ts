import { describe, expect, it } from 'vitest';
import { dominantKindColor, kindToColor } from './magicColor';

describe('magicColor', () => {
  it('maps every kind to a color', () => {
    expect(kindToColor.document).toBe('#a9cbe4');
    expect(kindToColor.folder).toBe('#9ecfc0');
  });

  it('returns the color of the most frequent kind', () => {
    const items = [
      { kind: 'document' },
      { kind: 'image' },
      { kind: 'document' },
      { kind: 'image' },
      { kind: 'image' },
    ];
    expect(dominantKindColor(items)).toBe('#c5b7df');
  });

  it('returns null for an empty list', () => {
    expect(dominantKindColor([])).toBeNull();
  });
});
