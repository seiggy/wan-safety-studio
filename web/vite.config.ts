/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The backend validates Host/Origin against localhost:51881, so changeOrigin must stay false.
const target = process.env.API_URL ?? 'http://localhost:51882';

export default defineConfig({
  base: '/',
  plugins: [react()],
  server: {
    host: 'localhost',
    port: 51881,
    strictPort: true,
    proxy: {
      '/api': { target, changeOrigin: false },
      '/auth': { target, changeOrigin: false },
    },
  },
  build: {
    outDir: 'dist',
    // CSP forbids inline scripts, styles and data: assets in script/style contexts.
    assetsInlineLimit: 0,
    cssCodeSplit: false,
    modulePreload: { polyfill: false },
  },
  test: {
    environment: 'jsdom',
    globals: false,
    include: ['src/**/*.test.{ts,tsx}'],
  },
});
