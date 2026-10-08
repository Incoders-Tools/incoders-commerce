import { describe, expect, it } from 'vitest'
import { getOrganizationThemeOverrides, isValidHexColor, pickReadableForeground } from './organizationTheme'

describe('isValidHexColor', () => {
  it('accepts a 6-digit hex color', () => {
    expect(isValidHexColor('#336699')).toBe(true)
  })

  it('rejects anything else', () => {
    expect(isValidHexColor('336699')).toBe(false)
    expect(isValidHexColor('#369')).toBe(false)
    expect(isValidHexColor('')).toBe(false)
  })
})

describe('pickReadableForeground', () => {
  it('picks a light foreground for a dark background', () => {
    expect(pickReadableForeground('#000000')).toBe('#fafafa')
  })

  it('picks a dark foreground for a light background', () => {
    expect(pickReadableForeground('#ffffff')).toBe('#0a0a0a')
  })
})

describe('getOrganizationThemeOverrides', () => {
  it('returns null when there is no primary color', () => {
    expect(getOrganizationThemeOverrides(null)).toBeNull()
  })

  it('returns null for a malformed color instead of throwing', () => {
    expect(getOrganizationThemeOverrides('not-a-color')).toBeNull()
  })

  it('maps a dark primary color onto --primary, a light --primary-foreground, and --ring', () => {
    expect(getOrganizationThemeOverrides('#0a0a0a')).toEqual({
      '--primary': '#0a0a0a',
      '--primary-foreground': '#fafafa',
      '--ring': '#0a0a0a',
    })
  })

  it('maps a light primary color onto a dark --primary-foreground', () => {
    expect(getOrganizationThemeOverrides('#f5f5f5')).toEqual({
      '--primary': '#f5f5f5',
      '--primary-foreground': '#0a0a0a',
      '--ring': '#f5f5f5',
    })
  })
})
