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
    // Same-origin auth cookie in dev: proxy API calls to Cloud.Api, which
    // always binds 8080 by default (Program.cs: `PORT` env var, or "8080"
    // when unset — dotnet run sets no PORT locally). Note: the sign-in
    // cookie is CookieSecurePolicy.Always (Program.cs), so it is dropped by
    // the browser unless this dev server itself is fronted by HTTPS (see
    // deploy/dev/run-all.ps1's local-ssl-proxy step / README.md).
    proxy: {
      '/catalog': 'http://localhost:8080',
      '/orders': 'http://localhost:8080',
      '/sync': 'http://localhost:8080',
      '/account': 'http://localhost:8080',
    },
  },
})
