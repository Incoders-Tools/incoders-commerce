const HEX_COLOR_PATTERN = /^#[0-9a-fA-F]{6}$/

export function isValidHexColor(value: string): boolean {
  return HEX_COLOR_PATTERN.test(value)
}

function srgbChannelToLinear(channel: number): number {
  return channel <= 0.03928 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4
}

/**
 * WCAG relative luminance (0 = black, 1 = white) of a `#rrggbb` color.
 * Caller must validate the color first (`isValidHexColor`).
 */
function relativeLuminance(hex: string): number {
  const r = parseInt(hex.slice(1, 3), 16) / 255
  const g = parseInt(hex.slice(3, 5), 16) / 255
  const b = parseInt(hex.slice(5, 7), 16) / 255
  const [lr, lg, lb] = [r, g, b].map(srgbChannelToLinear)
  return 0.2126 * lr + 0.7152 * lg + 0.0722 * lb
}

// The luminance value at which black text and white text cross over as the
// higher-contrast choice against a given background — a widely used
// accessibility heuristic for picking readable text on an arbitrary color.
const LUMINANCE_THRESHOLD = 0.179

/** Near-black / near-white, matching the tone of this app's existing tokens. */
const DARK_FOREGROUND = '#0a0a0a'
const LIGHT_FOREGROUND = '#fafafa'

/** Picks a readable foreground color for a `#rrggbb` background. */
export function pickReadableForeground(hex: string): string {
  return relativeLuminance(hex) > LUMINANCE_THRESHOLD ? DARK_FOREGROUND : LIGHT_FOREGROUND
}

/**
 * The semantic token keys `getOrganizationThemeOverrides` sets, so callers
 * (ThemeProvider) know exactly which inline custom properties to clear when
 * leaving the custom theme.
 */
export const ORG_THEME_OVERRIDE_KEYS = ['--primary', '--primary-foreground', '--ring'] as const

/**
 * T6: builds the CSS custom property overrides for the "custom" theme from
 * the organization's `primaryColor`, layered on top of the light palette
 * (T2's documented custom base) rather than replacing it. Returns `null`
 * when there is no usable color — the organization has none set, or a
 * malformed value slipped through — so the caller (ThemeProvider) falls
 * back to plain light instead of applying a broken palette.
 */
export function getOrganizationThemeOverrides(primaryColor: string | null): Record<string, string> | null {
  if (!primaryColor || !isValidHexColor(primaryColor)) {
    return null
  }

  return {
    '--primary': primaryColor,
    '--primary-foreground': pickReadableForeground(primaryColor),
    '--ring': primaryColor,
  }
}
