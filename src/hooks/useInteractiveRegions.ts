import { useEffect, useRef } from 'react';
import { nativeWindow } from '../services/nativeDesktop';
import type { AppToast } from './useDesktopScan';

interface UseInteractiveRegionsOptions {
  native: boolean;
  interactionActive: boolean;
  draggingModal: boolean;
  showCategoryModal: boolean;
  showSettings: boolean;
  toast: AppToast;
}

export function useInteractiveRegions(options: UseInteractiveRegionsOptions) {
  const { native, interactionActive, draggingModal, showCategoryModal, showSettings, toast } = options;
  const regionSignature = useRef('');

  useEffect(() => {
    if (!native) return undefined;

    const measure = (element: HTMLElement, padding: number) => {
      const rect = element.getBoundingClientRect();
      return {
        left: Math.max(0, Math.floor(rect.left - padding)),
        top: Math.max(0, Math.floor(rect.top - padding)),
        right: Math.ceil(rect.right + padding),
        bottom: Math.ceil(rect.bottom + padding),
        radius: (element.classList.contains('toast') ? 14 : 12) + padding,
      };
    };

    const applyRegions = (
      regions: Array<{ left: number; top: number; right: number; bottom: number; radius: number }>,
      hitRegions: Array<{ left: number; top: number; right: number; bottom: number; radius: number }>,
      full: boolean,
      expandInput = false,
    ) => {
      const signature = full ? `full:${expandInput ? 'input' : 'visual'}` : JSON.stringify([regions, hitRegions]);
      if (signature === regionSignature.current) return;
      regionSignature.current = signature;
      void nativeWindow.setInteractiveRegions(regions, full, hitRegions, expandInput).catch(() => {
        regionSignature.current = '';
      });
    };

    let frame = 0;
    const syncRegions = () => {
      window.cancelAnimationFrame(frame);
      frame = window.requestAnimationFrame(() => {
        if (interactionActive || draggingModal) {
          applyRegions([], [], true, draggingModal);
          return;
        }
        const nodes = Array.from(document.querySelectorAll<HTMLElement>('.modal, .toast'));
        const visualRegions = nodes.map((element) => measure(element, element.classList.contains('modal') ? 14 : 8)).filter((rect) => rect.right > rect.left && rect.bottom > rect.top);
        const hitRegions = nodes.map((element) => measure(element, 4)).filter((rect) => rect.right > rect.left && rect.bottom > rect.top);
        applyRegions(visualRegions, hitRegions, false);
      });
    };

    syncRegions();
    const resizeObserver = new ResizeObserver(syncRegions);
    document.querySelectorAll<HTMLElement>('.modal, .toast').forEach((element) => resizeObserver.observe(element));
    window.addEventListener('resize', syncRegions);
    return () => {
      window.cancelAnimationFrame(frame);
      resizeObserver.disconnect();
      window.removeEventListener('resize', syncRegions);
    };
  }, [draggingModal, interactionActive, native, showCategoryModal, showSettings, toast]);
}
