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
    // when unset — dotnet run sets no PORT locally).
    //
    // This dev server is for fast UI iteration only. Sign-in does NOT work
    // through it: the cookie is CookieSecurePolicy.Always (Program.cs), and
    // a browser drops a Secure cookie on plain-HTTP localhost:5173. For
    // authenticated testing use deploy/dev/run-all.ps1, which serves a built
    // SPA from the API and fronts it with HTTPS on :5443.
    proxy: {
      '/catalog': 'http://localhost:8080',
      '/orders': 'http://localhost:8080',
      '/sync': 'http://localhost:8080',
      '/account': 'http://localhost:8080',
    },
  },
})
