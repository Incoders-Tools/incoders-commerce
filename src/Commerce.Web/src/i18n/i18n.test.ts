import { describe, expect, it } from 'vitest'
import i18next, { namespaces, resources } from './index'

function flattenKeys(value: unknown, prefix = ''): string[] {
  if (typeof value !== 'object' || value === null) return [prefix]
  return Object.entries(value).flatMap(([key, child]) =>
    flattenKeys(child, prefix ? `${prefix}.${key}` : key),
  )
}

describe('i18n resources', () => {
  it('gives every namespace the same key set in en as in es', () => {
    for (const namespace of namespaces) {
      const esKeys = flattenKeys(resources.es[namespace]).sort()
      const enKeys = flattenKeys(resources.en[namespace]).sort()
      expect(enKeys, `namespace "${namespace}" is missing keys in en`).toEqual(esKeys)
    }
  })

  it('starts in Spanish', () => {
    expect(i18next.language).toBe('es')
    expect(i18next.t('common:actions.save')).toBe('Guardar')
  })

  it('re-renders a label after switching language', async () => {
    expect(i18next.t('common:actions.save')).toBe('Guardar')
    await i18next.changeLanguage('en')
    expect(i18next.t('common:actions.save')).toBe('Save')
    await i18next.changeLanguage('es')
  })
})
