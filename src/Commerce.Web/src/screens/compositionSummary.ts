import type { TFunction } from 'i18next'
import type { RateComponent } from '@/api/types'

const percentFormatter = new Intl.NumberFormat('es-AR', { maximumFractionDigits: 2 })

export const formatPercent = (value: number): string => percentFormatter.format(value)

/**
 * One-line reading of a composition, e.g. `IVA 10,5 % + IB 2,5 % + Remarcación 35 % sobre base`.
 * A component charged on the running subtotal says so itself; the closing "sobre base" only
 * appears when every component is charged on the base price.
 */
export function summarizeComponents(components: RateComponent[], t: TFunction<'priceLists'>): string {
  if (components.length === 0) return t('breakdown.noComposition')
  const ordered = [...components].sort((a, b) => a.order - b.order)
  const parts = ordered.map((component) =>
    t(component.calculationBase === 'Subtotal' ? 'breakdown.componentOnSubtotal' : 'breakdown.component', {
      label: component.label,
      percentage: formatPercent(component.percentage),
    }),
  )
  const allOnBase = ordered.every((component) => component.calculationBase === 'Base')
  return allOnBase ? `${parts.join(' + ')} ${t('breakdown.onBase')}` : parts.join(' + ')
}
