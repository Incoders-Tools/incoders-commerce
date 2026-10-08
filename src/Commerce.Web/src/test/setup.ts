import '@testing-library/jest-dom/vitest'
import { initI18n } from '@/i18n'

initI18n()

// jsdom has no ResizeObserver; recharts' ResponsiveContainer needs one to mount.
if (typeof globalThis.ResizeObserver === 'undefined') {
  globalThis.ResizeObserver = class ResizeObserver {
    observe() {}
    unobserve() {}
    disconnect() {}
  }
}
