import { Moon, Palette, Sun, type LucideProps } from 'lucide-react'
import type { ComponentType } from 'react'
import { cn } from '@/lib/utils'
import { useTheme, type Theme } from '@/theme/ThemeProvider'

// T7: switched from hand-drawn inline SVGs to lucide-react (now installed
// for the nav/account-menu icons) so this control reads as the same design
// system as the rest of the shell.
const OPTIONS: { value: Theme; label: string; Icon: ComponentType<LucideProps> }[] = [
  { value: 'light', label: 'Light', Icon: Sun },
  { value: 'dark', label: 'Dark', Icon: Moon },
  { value: 'custom', label: 'Custom', Icon: Palette },
]

/**
 * Vercel-style 3-option segmented theme switcher. Mounted in AppLayout's
 * header for now (T3 will move it into an account dropdown menu).
 */
export function ThemeSwitcher() {
  const { theme, setTheme } = useTheme()

  return (
    <div
      role="radiogroup"
      aria-label="Theme"
      className="inline-flex items-center gap-0.5 rounded-md border border-border bg-muted p-0.5"
    >
      {OPTIONS.map(({ value, label, Icon }) => {
        const selected = theme === value
        return (
          <button
            key={value}
            type="button"
            role="radio"
            aria-checked={selected}
            aria-label={label}
            onClick={() => setTheme(value)}
            className={cn(
              'inline-flex h-7 items-center gap-1.5 rounded-sm px-2.5 text-xs font-medium transition-colors',
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
