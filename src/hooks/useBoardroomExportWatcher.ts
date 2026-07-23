import { useEffect, useRef } from 'react';
import { useStore } from '../store/useStore';

/**
 * Always-on Spooling Savant 3.0 export watcher (Electron). Imports boardroom-package.json into the
 * chosen Spooling task whenever the Exports folder changes — not only on Fab Workstation.
 */
export function useBoardroomExportWatcher() {
  const importBoardroomPackageManifest = useStore((s) => s.importBoardroomPackageManifest);
  const importInFlight = useRef(false);

  useEffect(() => {
    const api = window.electronAPI;
    if (!api?.watchBoardroomExports || !api.readBoardroomPackage || !api.onBoardroomExportsChanged) {
      return;
    }

    let cancelled = false;
    let unsub: (() => void) | undefined;

    const runImport = async (dirOrJsonPath: string) => {
      if (importInFlight.current || cancelled) return;
      importInFlight.current = true;
      try {
        const read = await api.readBoardroomPackage!(dirOrJsonPath);
        if (cancelled) return;
        importBoardroomPackageManifest(read.manifest, read.exportFolder);
      } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        // Empty folder / missing json / locked Ready-for-Fab replace are expected.
        if (!/not found|cannot replace|ready for fab/i.test(message)) {
          console.warn('Boardroom export import failed', error);
        }
      } finally {
        importInFlight.current = false;
      }
    };

    void (async () => {
      const watch = await api.watchBoardroomExports!();
      if (cancelled) return;
      if (!watch.ok) {
        console.warn('Boardroom exports watch unavailable', watch.error);
        return;
      }
      await runImport(watch.dir);
      unsub = api.onBoardroomExportsChanged!(({ dir, filename }) => {
        if (filename && !filename.toLowerCase().includes('boardroom-package')) return;
        void runImport(dir);
      });
    })();

    return () => {
      cancelled = true;
      unsub?.();
      void api.unwatchBoardroomExports?.();
    };
  }, [importBoardroomPackageManifest]);
}
