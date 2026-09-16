import path from 'node:path'
import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: {
      '@': path.resolve(import.meta.dirname, './src'),
    },
  },
  server: {
    // Same-origin auth cookie in dev: proxy API calls to Cloud.Api running on
    // 5080 (see appsettings.Development.json) so the browser sees one origin.
    proxy: {
      '/catalog': 'http://localhost:5080',
      '/orders': 'http://localhost:5080',
      '/sync': 'http://localhost:5080',
      '/account': 'http://localhost:5080',
    },
  },
})
