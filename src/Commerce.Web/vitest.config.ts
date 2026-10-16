import path from 'node:path'
import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vitest/config'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: {
      '@': path.resolve(import.meta.dirname, './src'),
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    // Real-browser Playwright specs live under e2e/ and are run separately
    // via `npm run test:e2e` (playwright.config.ts) — Vitest must never pick
    // them up (they call test.describe from @playwright/test, not Vitest's).
    exclude: ['**/node_modules/**', 'e2e/**'],
  },
})
