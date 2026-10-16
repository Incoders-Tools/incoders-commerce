import type { SalesChannel } from '@/dashboard/types'

/**
 * One fixed colour per channel (categorical slots 1-3, see `index.css`):
 * colour follows the entity, so it is identical in the trend chart, its
 * legend and the delivery/counter split.
 */
export const channelMeta: Record<SalesChannel, { color: string; labelKey: string }> = {
  counter: { color: 'var(--chart-1)', labelKey: 'channels.counter' },
  delivery: { color: 'var(--chart-2)', labelKey: 'channels.delivery' },
  web: { color: 'var(--chart-3)', labelKey: 'channels.web' },
}

export const channelOrder: readonly SalesChannel[] = ['counter', 'delivery', 'web']
