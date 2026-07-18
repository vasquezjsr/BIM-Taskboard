import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { devStoreSyncPlugin } from './vite-plugin-dev-store-sync';

const ignoredWatchPath = (watchPath: string) => {
  const normalized = watchPath.replace(/\\/g, '/');
  return (
    normalized.includes('node_modules') ||
    normalized.includes('Spooling Savant Version 3 (Exports)') ||
    // Temp CDP helper scripts in repo root lock on Windows and crash Vite (EBUSY).
    (/cdp/i.test(normalized) && /\.(?:js|cjs|mjs)$/i.test(normalized))
  );
};

export default defineConfig({
  plugins: [react(), devStoreSyncPlugin()],
  base: './',
  build: {
    outDir: 'dist',
  },
  server: {
    port: 5173,
    strictPort: true,
    watch: {
      // Nested Revit addin + Boardroom export outputs lock files; watching them crashes Vite (EBUSY).
      ignored: ignoredWatchPath,
    },
  },
});
