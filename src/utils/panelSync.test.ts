import { describe, expect, it } from 'vitest';
import { buildPanelSyncKey, hashPanelValue } from './panelSync';

describe('panel synchronization keys', () => {
  it('is stable for equivalent panel data', () => {
    const panel = { id: 'documents', x: 10, y: 20, items: [{ id: 'a', name: 'A', iconUrl: 'data:image/png;base64,AAAA' }] };
    const clone = { ...panel, items: panel.items.map((item) => ({ ...item })) };
    expect(buildPanelSyncKey([panel])).toBe(buildPanelSyncKey([clone]));
  });

  it('changes when geometry or same-length icon content changes', () => {
    const base = { id: 'documents', x: 10, y: 20, items: [{ id: 'a', name: 'A', iconUrl: 'AAAA' }] };
    expect(buildPanelSyncKey([base])).not.toBe(buildPanelSyncKey([{ ...base, x: 11 }]));
    expect(buildPanelSyncKey([base])).not.toBe(buildPanelSyncKey([{ ...base, items: [{ ...base.items[0], iconUrl: 'BBBB' }] }]));
  });

  it('changes when the global capsule mode changes', () => {
    const base = { id: 'documents', capsuleMode: false, items: [] };
    expect(buildPanelSyncKey([base])).not.toBe(buildPanelSyncKey([{ ...base, capsuleMode: true }]));
  });

  it('changes when the ui scale changes so the native panel is re-pushed', () => {
    const base = { id: 'documents', uiScale: 100, items: [] };
    expect(buildPanelSyncKey([base])).not.toBe(buildPanelSyncKey([{ ...base, uiScale: 130 }]));
    expect(buildPanelSyncKey([base])).toBe(buildPanelSyncKey([{ ...base, uiScale: 100 }]));
  });

  it('uses a compact deterministic icon hash', () => {
    expect(hashPanelValue('same')).toBe(hashPanelValue('same'));
    expect(hashPanelValue('same')).not.toBe(hashPanelValue('diff'));
    expect(hashPanelValue(undefined)).toBe('0');
  });
});
