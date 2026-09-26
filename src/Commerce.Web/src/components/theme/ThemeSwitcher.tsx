import { Moon, Palette, Sun, type LucideProps } from 'lucide-react'
import type { ComponentType } from 'react'
import { useTranslation } from 'react-i18next'
import { cn } from '@/lib/utils'
import { useOrganizationBranding } from '@/theme/OrganizationBrandingProvider'
import { useTheme, type Theme } from '@/theme/ThemeProvider'

// T7: switched from hand-drawn inline SVGs to lucide-react (now installed
// for the nav/account-menu icons) so this control reads as the same design
// system as the rest of the shell.
const OPTIONS: { value: Theme; labelKey: 'light' | 'dark' | 'custom'; Icon: ComponentType<LucideProps> }[] = [
  { value: 'light', labelKey: 'light', Icon: Sun },
  { value: 'dark', labelKey: 'dark', Icon: Moon },
  { value: 'custom', labelKey: 'custom', Icon: Palette },
]

/**
 * Vercel-style 3-option segmented theme switcher. Mounted in AppLayout's
 * header for now (T3 will move it into an account dropdown menu).
 */
export function ThemeSwitcher() {
  const { t } = useTranslation('theme')
  const { theme, setTheme } = useTheme()
  const { branding } = useOrganizationBranding()
  const customAvailable = Boolean(branding?.primaryColor)

  return (
    <div
      role="radiogroup"
      aria-label={t('label')}
      className="inline-flex items-center gap-0.5 rounded-md border border-border bg-muted p-0.5"
    >
      {OPTIONS.map(({ value, labelKey, Icon }) => {
        const selected = theme === value
        const label = t(`options.${labelKey}`)
        // T6: "Custom" needs an organization primary color to mean
        // anything — disabled (not hidden, so its accessible name stays
        // discoverable) rather than crashing or silently doing nothing.
        const disabled = value === 'custom' && !customAvailable
        return (
          <button
            key={value}
            type="button"
            role="radio"
            aria-checked={selected}
            aria-label={label}
            disabled={disabled}
            title={disabled ? t('noCustomColorExplanation') : undefined}
            onClick={() => setTheme(value)}
            className={cn(
              'inline-flex h-7 items-center gap-1.5 rounded-sm px-2.5 text-xs font-medium transition-colors disabled:cursor-not-allowed disabled:opacity-50',
              selected
                ? 'bg-background text-foreground shadow-sm'
                : 'text-muted-foreground hover:text-foreground',
            )}
          >
            <Icon aria-hidden="true" className="size-3.5 shrink-0" />
            <span>{label}</span>
          </button>
        )
      })}
    </div>
  )
}
