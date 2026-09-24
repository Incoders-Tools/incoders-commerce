/**
 * Integration point for T6 (org-level custom theme). Organization settings
 * (logo, color palette, etc.) don't exist yet — that's T5's backend plus
 * OrganizationsScreen rebuild. Until the authenticated session exposes a
 * resolved organization theme, this always returns `null`, and
 * `ThemeProvider` treats "custom" as a safe fallback to "light" instead of
 * applying nothing or breaking the page.
 *
 * When T6 lands, this will read the sysadmin-assigned org colors (from the
 * session/org-settings response) and return them as CSS custom property
 * name/value pairs, which `ThemeProvider` will inject as inline `style` on
 * `<html>` (or a generated `<style>` tag) instead of toggling the `dark`
 * class — a custom palette is independent of the light/dark token set.
 */
export function getOrganizationThemeOverrides(): Record<string, string> | null {
  return null
}
