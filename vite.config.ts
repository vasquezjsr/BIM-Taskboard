import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { devStoreSyncPlugin } from './vite-plugin-dev-store-sync';

export default defineConfig({
  plugins: [react(), devStoreSyncPlugin()],
  base: './',
  build: {
    outDir: 'dist',
  },
  server: {
    port: 5173,
    strictPort: true,
  },
});
