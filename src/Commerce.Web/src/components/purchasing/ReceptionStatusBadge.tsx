import { useTranslation } from 'react-i18next'
import type { ReceptionStatus } from '@/api/types'
import { cn } from '@/lib/utils'

const styles: Record<ReceptionStatus, string> = {
  Draft: 'border-border bg-muted text-muted-foreground',
  Confirmed: 'border-emerald-500/40 bg-emerald-500/10 text-emerald-700 dark:text-emerald-400',
  Voided: 'border-destructive/40 bg-destructive/10 text-destructive',
}

export function ReceptionStatusBadge({ status }: { status: ReceptionStatus }) {
  const { t } = useTranslation('purchases')
  return (
    <span className={cn('inline-flex rounded-full border px-2 py-0.5 text-xs font-medium', styles[status])}>
      {t(`status.${status}`)}
    </span>
  )
}
