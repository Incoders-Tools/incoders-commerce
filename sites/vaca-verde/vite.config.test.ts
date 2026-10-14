import type { ConfigEnv, UserConfigFnObject } from 'vite'
import { afterEach, describe, expect, it, vi } from 'vitest'
import config, { resolveAppUrl } from './vite.config.ts'

const configFor = (env: Pick<ConfigEnv, 'command' | 'mode'>) => (config as UserConfigFnObject)({ ...env, isSsrBuild: false, isPreview: false })

describe('the site config', () => {
  afterEach(() => vi.unstubAllEnvs())

  it('refuses a build in any mode without VITE_APP_URL, and lets the preview serve what was built', () => {
    vi.stubEnv('VITE_APP_URL', '')
    expect(() => configFor({ command: 'build', mode: 'staging' })).toThrow(/VITE_APP_URL is required/)
    expect(() => configFor({ command: 'build', mode: 'production' })).toThrow(/VITE_APP_URL is required/)
    expect(() => configFor({ command: 'serve', mode: 'production' })).not.toThrow()
  })
})

describe('resolveAppUrl', () => {
  it('refuses any build without the web app origin, whatever its mode', () => {
    expect(() => resolveAppUrl(true, undefined)).toThrow(/VITE_APP_URL is required/)
    expect(() => resolveAppUrl(true, '  ')).toThrow(/VITE_APP_URL is required/)
  })

  it('points the dev server at the local stack when none is given', () => {
    expect(resolveAppUrl(false, undefined)).toBe('https://localhost:5443')
  })

  it('keeps the https origin only, and refuses plain http', () => {
    expect(resolveAppUrl(true, 'https://app.vacaverde.com.ar/login/')).toBe('https://app.vacaverde.com.ar')
    expect(() => resolveAppUrl(true, 'http://app.vacaverde.com.ar')).toThrow(/must use https/)
  })
})
