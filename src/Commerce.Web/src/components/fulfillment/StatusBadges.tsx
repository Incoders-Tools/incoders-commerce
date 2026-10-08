import { useTranslation } from 'react-i18next'
import type { OrderStatus, RunStatus } from '@/api/fulfillment'
import { cn } from '@/lib/utils'

const orderStyles: Record<OrderStatus, string> = {
  Confirmed: 'border-border bg-muted text-muted-foreground',
  InPreparation: 'border-amber-500/40 bg-amber-500/10 text-amber-700 dark:text-amber-400',
  ReadyToDispatch: 'border-sky-500/40 bg-sky-500/10 text-sky-700 dark:text-sky-400',
  OutForDelivery: 'border-indigo-500/40 bg-indigo-500/10 text-indigo-700 dark:text-indigo-400',
  Delivered: 'border-emerald-500/40 bg-emerald-500/10 text-emerald-700 dark:text-emerald-400',
  PartiallyDelivered: 'border-emerald-500/40 bg-emerald-500/5 text-emerald-700 dark:text-emerald-400',
  Cancelled: 'border-destructive/40 bg-destructive/10 text-destructive',
}

const runStyles: Record<RunStatus, string> = {
  Planned: 'border-border bg-muted text-muted-foreground',
  OutForDelivery: 'border-indigo-500/40 bg-indigo-500/10 text-indigo-700 dark:text-indigo-400',
  Completed: 'border-emerald-500/40 bg-emerald-500/10 text-emerald-700 dark:text-emerald-400',
}

const base = 'inline-flex whitespace-nowrap rounded-full border px-2 py-0.5 text-xs font-medium'

export function OrderStatusBadge({ status }: { status: OrderStatus }) {
  const { t } = useTranslation('fulfillment')
  return <span className={cn(base, orderStyles[status])}>{t(`status.${status}`)}</span>
}

export function RunStatusBadge({ status }: { status: RunStatus }) {
  const { t } = useTranslation('fulfillment')
  return <span className={cn(base, runStyles[status])}>{t(`runStatus.${status}`)}</span>
}
