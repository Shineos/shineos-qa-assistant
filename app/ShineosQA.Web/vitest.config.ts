import { defineConfig } from 'vitest/config';

// report.ts が #modal-host / innerHTML / navigator.clipboard / window.open を
// 使うため DOM 環境(jsdom)必須。本設定はテスト専用(build は vite.config.ts)。
export default defineConfig({
  test: {
    environment: 'jsdom',
  },
});
