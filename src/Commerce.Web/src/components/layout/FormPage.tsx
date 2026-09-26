import { ArrowLeft } from 'lucide-react'
import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { cn } from '@/lib/utils'

interface FormPageProps {
  title: string
  description?: string
  /** Returns to whatever screen opened this page (usually the list it replaced). */
  onBack: () => void
  /** Accessible name of the back action, e.g. "Back to customers". */
  backLabel?: string
  /** The form (or detail content) itself. */
  children: ReactNode
  /** Optional actions row pinned under the body, e.g. Save/Cancel. */
  footer?: ReactNode
  className?: string
}

/**
 * T9: shared full-screen shell for the forms and detail pages that used to
 * render as a boxed inline expansion (`DataView.renderExpanded`) or a
 * centered `Card` — the user's complaint was that both read like an
 * embedded modal instead of using the whole screen. `FormPage` is purely
 * presentational (container-presentational pattern): it owns the header
 * (back action + title/description) and body layout, never data fetching or
 * submit handling, which stay with the screen/form that renders it.
 *
 * The back action is additive, not a replacement for a form's own Cancel
 * button — `CustomerForm`/`OrganizationForm` keep their existing Cancel
 * button (its accessible name is asserted by `OrganizationsScreen.test.tsx`
 * and reachable via `e2e/system-admin.spec.ts`'s flow), so this header gives
 * a second, always-visible way back without renaming or removing that one.
 */
export function FormPage({ title, description, onBack, backLabel, children, footer, className }: FormPageProps) {
  const { t } = useTranslation('common')
  return (
    <section className={cn('flex w-full flex-col gap-6', className)}>
      <div className="flex flex-col gap-3">
        <button
          type="button"
          onClick={onBack}
          className="inline-flex w-fit items-center gap-1.5 text-sm text-muted-foreground transition-colors hover:text-foreground"
        >
          <ArrowLeft className="size-4 shrink-0" aria-hidden="true" />
          {backLabel ?? t('actions.back')}
        </button>
        <div className="min-w-0">
          <h1 className="text-2xl font-semibold tracking-tight text-foreground">{title}</h1>
          {description && <p className="mt-1 text-sm text-muted-foreground">{description}</p>}
        </div>
      </div>

      <div className="w-full">{children}</div>

      {footer && <div className="flex items-center gap-2 border-t border-border pt-4">{footer}</div>}
    </section>
  )
}
