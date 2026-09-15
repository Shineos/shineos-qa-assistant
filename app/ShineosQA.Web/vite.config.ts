import { defineConfig } from 'vite';

export default defineConfig({
  base: '/', // 絶対パス必須: /c/{uuid} のSPAルートでもアセットが解決される（相対だと404で無音死）
  build: { outDir: 'dist', emptyOutDir: true, target: 'es2022' },
  server: {
    port: 5173,
    proxy: { '/api': 'http://127.0.0.1:8300', '/health': 'http://127.0.0.1:8300' },
  },
});
