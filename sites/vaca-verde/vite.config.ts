import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { defineConfig, loadEnv } from 'vite'

/** HTTPS origin served by deploy/dev/run-all.ps1, where the Commerce web app runs locally. */
const LOCAL_APP_URL = 'https://localhost:5443'

/**
 * Origin of the Commerce web app the access buttons point to (`/order`, `/login`).
 * A production build without it fails instead of publishing a site whose buttons go nowhere.
 */
export function resolveAppUrl(isProductionBuild: boolean, raw: string | undefined): string {
  const value = raw?.trim()
  if (!value) {
    if (isProductionBuild) {
      throw new Error('VITE_APP_URL is required for a production build (e.g. https://app.vacaverde.com.ar).')
    }
    return LOCAL_APP_URL
  }
  const url = new URL(value)
  if (url.protocol !== 'https:') {
    throw new Error(`VITE_APP_URL must use https, got "${value}".`)
  }
  return url.origin
}

export default defineConfig(({ command, mode }) => {
  const env = loadEnv(mode, import.meta.dirname, 'VITE_')
  // `vite preview` also runs in production mode but only serves dist/, which already has the URL baked in.
  const isProductionBuild = command === 'build' && mode === 'production'
  return {
    plugins: [react(), tailwindcss()],
    define: {
      __APP_URL__: JSON.stringify(resolveAppUrl(isProductionBuild, env.VITE_APP_URL)),
    },
  }
})
