import { useCallback, useEffect, useRef, useState } from 'react';
import { buildOrganizationPlan, matchOrganizationRule } from '../services/classifier';
import { executeOrganization, isNativeRuntime, restoreOrganization, scanDesktop, type OrganizationHistoryEntry } from '../services/nativeDesktop';
import type { Category, OrganizationRule, ScanResult } from '../types';
import { fallbackRoot, organizationHistoryKey } from '../state/keys';
import { demoItems } from '../data/defaults';

export type AppToast = { message: string; type?: 'success' | 'info' } | null;

interface UseDesktopScanOptions {
  categories: Category[];
  showHiddenFiles: boolean;
  automaticScan: boolean;
  automaticOrganize: boolean;
  organizeDelaySeconds: number;
  organizeMode: 'move' | 'reference';
  referencePins: Record<string, string[]>;
  rules: OrganizationRule[];
  interactionActive: boolean;
  setToast: (toast: AppToast) => void;
}

export function useDesktopScan({
  categories,
  showHiddenFiles,
  automaticScan,
  automaticOrganize,
  organizeDelaySeconds,
  organizeMode,
  referencePins,
  rules,
  interactionActive,
  setToast,
}: UseDesktopScanOptions) {
  const [scan, setScan] = useState<ScanResult>({
    desktopPath: fallbackRoot,
    // 原生模式下首屏只同步真实桌面；演示数据仅用于浏览器开发预览，避免启动时把演示项闪进分区。
    items: isNativeRuntime() ? [] : demoItems,
    scannedAt: new Date().toISOString(),
    isNative: false,
  });
  const [isScanning, setIsScanning] = useState(false);
  const scanInFlight = useRef(false);
  const refreshQueued = useRef(false);
  const organizingInFlight = useRef(false);
  const organizeCandidates = useRef<Record<string, { signature: string; stableSince: number }>>({});

  // 引用模式：把钉选路径强制归属到对应分类；不在扫描范围内的路径忽略
  const applyReferencePins = useCallback((result: ScanResult): ScanResult => {
    if (organizeMode !== 'reference') return result;
    const pinOwner = new Map<string, string>();
    Object.entries(referencePins).forEach(([categoryId, paths]) => {
      paths.forEach((path) => { if (!pinOwner.has(path)) pinOwner.set(path, categoryId); });
    });
    if (pinOwner.size === 0) return result;
    let changed = false;
    const items = result.items.map((item) => {
      const categoryId = pinOwner.get(item.path);
      if (!categoryId || categoryId === item.categoryId) return item;
      changed = true;
      return { ...item, categoryId };
    });
    return changed ? { ...result, items } : result;
  }, [organizeMode, referencePins]);

  const runScan = useCallback(async (quiet = false) => {
    if (scanInFlight.current) {
      refreshQueued.current ||= !quiet;
      return;
    }
    scanInFlight.current = true;
    if (!quiet) setIsScanning(true);
    try {
      setScan(applyReferencePins(await scanDesktop(categories, showHiddenFiles, !quiet)));
      if (!quiet) setToast({ message: '桌面分区已更新', type: 'success' });
    } catch (error) {
      setToast({ message: error instanceof Error ? error.message : '桌面扫描失败', type: 'info' });
    } finally {
      scanInFlight.current = false;
      setIsScanning(false);
      if (refreshQueued.current) {
        refreshQueued.current = false;
        void runScan(false);
      }
    }
  }, [applyReferencePins, categories, setToast, showHiddenFiles]);

  const organizeNow = useCallback(async (onlyStable = false) => {
    if (organizeMode === 'reference') {
      // 自动触发静默跳过；手动触发时给出提示
      if (!onlyStable) setToast({ message: '引用模式下不移动文件，无需收纳', type: 'info' });
      return;
    }
    if (organizingInFlight.current || interactionActive) return;
    const now = Date.now();
    const pending = scan.items.filter((item) => !item.managed && (item.categoryId !== 'uncategorized' || matchOrganizationRule(item, rules) !== undefined));
    const nextCandidates: Record<string, { signature: string; stableSince: number }> = {};
    const ready = pending.filter((item) => {
      const signature = `${item.size}:${item.modifiedAt}`;
      const previous = organizeCandidates.current[item.path];
      const stableSince = previous?.signature === signature ? previous.stableSince : now;
      nextCandidates[item.path] = { signature, stableSince };
      return !onlyStable || now - stableSince >= organizeDelaySeconds * 1000;
    });
    organizeCandidates.current = nextCandidates;
    if (!ready.length) return;
    const plan = buildOrganizationPlan(ready, categories, scan.desktopPath, rules);
    if (!onlyStable) {
      const preview = plan.slice(0, 8).map((move) => `• ${move.item.name}`).join('\n');
      const remainder = plan.length > 8 ? `\n……另有 ${plan.length - 8} 个项目` : '';
      if (!window.confirm(`将移动 ${plan.length} 个已匹配项目到“片刻收纳”：\n\n${preview}${remainder}\n\n是否继续？`)) return;
    }
    organizingInFlight.current = true;
    try {
      const results = await executeOrganization(plan, categories, scan.desktopPath);
      const moved = results.filter((result) => result.status === 'moved').length;
      const failed = results.length - moved;
      if (moved) {
        const history: OrganizationHistoryEntry[] = results
          .filter((result) => result.status === 'moved')
          .map((result) => ({ source: result.source, destination: result.destination, name: result.item.name }));
        let historySaved = true;
        try {
          localStorage.setItem(organizationHistoryKey, JSON.stringify(history));
        } catch {
          historySaved = false;
        }
        if (!onlyStable || !historySaved) {
          setToast({
            message: `已自动收纳 ${moved} 个项目${failed ? `，${failed} 个失败` : ''}${historySaved ? '' : '，但撤销记录保存失败'}`,
            type: failed || !historySaved ? 'info' : 'success',
          });
        }
        await runScan(true);
      }
      if (failed && !moved) setToast({ message: `${failed} 个项目均未能收纳，请检查目录权限或文件占用`, type: 'info' });
    } catch (error) {
      setToast({ message: error instanceof Error ? error.message : '自动收纳失败', type: 'info' });
    } finally {
      organizingInFlight.current = false;
    }
  }, [categories, interactionActive, organizeDelaySeconds, organizeMode, runScan, rules, scan.desktopPath, scan.items, setToast]);

  const undoLastOrganization = useCallback(async () => {
    if (organizeMode === 'reference') {
      setToast({ message: '引用模式下不移动文件，无需收纳', type: 'info' });
      return;
    }
    if (organizingInFlight.current || interactionActive) return;
    let history: OrganizationHistoryEntry[] = [];
    try {
      const parsed = JSON.parse(localStorage.getItem(organizationHistoryKey) ?? '[]') as unknown;
      if (Array.isArray(parsed)) {
        history = parsed.filter((entry): entry is OrganizationHistoryEntry => Boolean(
          entry && typeof entry === 'object' &&
          typeof (entry as OrganizationHistoryEntry).source === 'string' &&
          typeof (entry as OrganizationHistoryEntry).destination === 'string' &&
          typeof (entry as OrganizationHistoryEntry).name === 'string',
        ));
      }
    } catch {
      localStorage.removeItem(organizationHistoryKey);
    }
    if (!history.length) {
      setToast({ message: '没有可撤销的收纳记录', type: 'info' });
      return;
    }
    if (!window.confirm(`将尝试把上次收纳的 ${history.length} 个项目放回原位置，是否继续？`)) return;
    organizingInFlight.current = true;
    try {
      const results = await restoreOrganization(history);
      const restored = results.filter((result) => result.status === 'restored').length;
      const failures = results.filter((result) => result.status === 'failed');
      if (failures.length) {
        localStorage.setItem(organizationHistoryKey, JSON.stringify(failures.map(({ source, destination, name }) => ({ source, destination, name }))));
      } else {
        localStorage.removeItem(organizationHistoryKey);
      }
      setToast({
        message: `已撤销 ${restored} 个项目${failures.length ? `，${failures.length} 个失败` : ''}`,
        type: failures.length ? 'info' : 'success',
      });
      if (restored) await runScan(true);
    } catch (error) {
      setToast({ message: error instanceof Error ? error.message : '撤销收纳失败', type: 'info' });
    } finally {
      organizingInFlight.current = false;
    }
  }, [interactionActive, organizeMode, runScan, setToast]);

  useEffect(() => {
    if (!automaticScan && !automaticOrganize) return undefined;
    const interval = automaticOrganize
      ? Math.max(4_000, Math.min(12_000, organizeDelaySeconds * 500))
      : 30_000;
    const timer = window.setInterval(() => {
      if (!interactionActive) void runScan(true);
    }, interval);
    return () => window.clearInterval(timer);
  }, [automaticOrganize, automaticScan, interactionActive, organizeDelaySeconds, runScan]);

  useEffect(() => {
    // 引用模式不做自动收纳：钉选归属只影响视图，不移动文件
    if (!automaticOrganize || organizeMode !== 'move' || !scan.isNative || interactionActive) return;
    void organizeNow(true);
  }, [automaticOrganize, interactionActive, organizeMode, organizeNow, scan.isNative, scan.scannedAt]);

  useEffect(() => {
    const organize = () => void organizeNow(false);
    const undo = () => void undoLastOrganization();
    window.addEventListener('pivkey-organize-now', organize);
    window.addEventListener('pivkey-undo-organize', undo);
    return () => {
      window.removeEventListener('pivkey-organize-now', organize);
      window.removeEventListener('pivkey-undo-organize', undo);
    };
  }, [organizeNow, undoLastOrganization]);

  return {
    scan,
    setScan,
    isScanning,
    runScan,
    organizeNow,
    undoLastOrganization,
    scanInFlight,
  };
}
