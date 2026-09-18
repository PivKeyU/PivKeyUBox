import { describe, expect, it } from 'vitest';
import { defaultCategories } from '../data/defaults';
import { buildOrganizationPlan, classifyKind, getExtension, matchCategory, sortDesktopItems } from './classifier';

describe('classifier', () => {
  it('extracts extensions without treating dotfiles as documents', () => {
    expect(getExtension('report.Final.PDF')).toBe('pdf');
    expect(getExtension('.gitignore')).toBe('');
  });

  it('recognizes shortcuts, media, documents and folders', () => {
    expect(classifyKind('lnk', false)).toBe('shortcut');
    expect(classifyKind('png', false)).toBe('image');
    expect(classifyKind('docx', false)).toBe('document');
    expect(classifyKind('', true)).toBe('folder');
  });

  it('matches custom extension categories before falling back', () => {
    const categories = [{ id: 'code', name: '代码', icon: 'code', color: '#000', extensions: ['tsx'] }, ...defaultCategories];
    expect(matchCategory('tsx', false, categories)).toBe('code');
    expect(matchCategory('unknown', false, categories)).toBe('uncategorized');
  });

  it('builds moves inside the managed desktop folder', () => {
    const items = [{
      id: '1', name: 'a.pdf', path: 'C:\\Desk\\a.pdf', extension: 'pdf', kind: 'document' as const,
      size: 1, modifiedAt: '', categoryId: 'documents',
    }];
    const plan = buildOrganizationPlan(items, defaultCategories, 'C:\\Desk');
    expect(plan[0].destination).toBe('C:\\Desk\\片刻收纳\\工作文档\\a.pdf');
  });

  it('sorts by name, newest time and largest size without mutating input', () => {
    const items = [
      { id: '1', name: 'B.txt', path: 'B', extension: 'txt', kind: 'document' as const, size: 20, modifiedAt: '2026-01-01T00:00:00Z', categoryId: 'documents' },
      { id: '2', name: 'A.txt', path: 'A', extension: 'txt', kind: 'document' as const, size: 10, modifiedAt: '2026-02-01T00:00:00Z', categoryId: 'documents' },
      { id: '3', name: 'C.txt', path: 'C', extension: 'txt', kind: 'document' as const, size: 30, modifiedAt: '2025-12-01T00:00:00Z', categoryId: 'documents' },
    ];
    expect(sortDesktopItems(items, 'name').map((item) => item.name)).toEqual(['A.txt', 'B.txt', 'C.txt']);
    expect(sortDesktopItems(items, 'modified').map((item) => item.name)).toEqual(['A.txt', 'B.txt', 'C.txt']);
    expect(sortDesktopItems(items, 'size').map((item) => item.name)).toEqual(['C.txt', 'B.txt', 'A.txt']);
    expect(items.map((item) => item.name)).toEqual(['B.txt', 'A.txt', 'C.txt']);
  });
});
