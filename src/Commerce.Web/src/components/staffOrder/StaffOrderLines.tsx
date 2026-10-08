import { useTranslation } from 'react-i18next'
import { Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import type { StaffOrderQuoteLine, StaffPresentationOption } from '@/api/types'
import { formatMoney } from '@/dashboard/format'
import { unitLabel } from '@/lib/quantity'
import { cn } from '@/lib/utils'
import { useNumberFormat } from '@/organization/NumberFormatContext'
import { presentationName } from './presentationName'

export interface StaffOrderLineView {
  option: StaffPresentationOption
  quantityText: string
  /** Why the typed quantity cannot be used, or `null`. */
  error: string | null
  /** The quoted line for exactly this quantity, or `undefined` while it is being priced. */
  quote: StaffOrderQuoteLine | undefined
}

/**
 * The draft's lines: one card per presentation with its quantity (in the organization's number format; whole
 * units for fixed-quantity products) and, once quoted, its unit net price, line total and the list that priced it.
 * Written for the staff quote API: `OrderLinesEditor` serves the catalog-driven public order instead.
 */
export function StaffOrderLines({
  lines,
  awaitingQuote,
  onQuantityChange,
  onRemove,
}: {
  lines: StaffOrderLineView[]
  /** A quote is expected (a customer is chosen): unpriced lines read "pricing" rather than nothing. */
  awaitingQuote: boolean
  onQuantityChange: (presentationId: string, text: string) => void
  onRemove: (presentationId: string) => void
}) {
  const { t } = useTranslation('orders')
  const numberFormat = useNumberFormat()

  if (lines.length === 0) {
    return (
      <p className="rounded-lg border border-dashed border-border px-4 py-6 text-center text-sm text-muted-foreground">
        {t('staffOrder.products.empty')}
      </p>
    )
  }

  return (
    <ul aria-label={t('staffOrder.products.linesLabel')} className="flex flex-col gap-3">
      {lines.map(({ option, quantityText, error, quote }) => {
        const name = presentationName(option)
        const inputId = `quantity-${option.presentationId}`
        const unpriced = quote?.status === 'no-effective-price'
        return (
          <li
            key={option.presentationId}
            aria-label={name}
            className={cn('flex flex-col gap-3 rounded-lg border bg-card p-4', unpriced ? 'border-destructive/50' : 'border-border')}
          >
            <div className="flex items-start justify-between gap-3">
              <div className="min-w-0">
                <p className="font-medium text-foreground">{option.productName}</p>
                <p className="text-sm text-muted-foreground">
                  {[option.presentationName, option.identificationCode].filter(Boolean).join(' · ')}
                </p>
                <LinePrice quote={quote} />
              </div>
              <Button
                variant="outline"
                aria-label={t('staffOrder.lines.remove', { name })}
                className="size-11 shrink-0 p-0 lg:size-9"
                onClick={() => onRemove(option.presentationId)}
              >
                <Trash2 aria-hidden="true" className="size-4" />
              </Button>
            </div>

            <div className="flex items-start justify-between gap-3">
              <div className="flex flex-col gap-1">
                <div className="flex items-center gap-2">
                  <Input
                    id={inputId}
                    aria-label={t('staffOrder.lines.quantityLabel', { name })}
                    aria-invalid={error ? true : undefined}
                    aria-describedby={error ? `${inputId}-error` : undefined}
                    inputMode={option.quantityBehavior === 'FixedQuantity' ? 'numeric' : 'decimal'}
                    placeholder={option.quantityBehavior === 'FixedQuantity' ? undefined : numberFormat.example}
                    className="h-11 w-24 text-right text-base tabular-nums lg:h-9 lg:text-sm"
                    value={quantityText}
                    onChange={(event) => onQuantityChange(option.presentationId, event.target.value)}
                  />
                  <span className="text-sm text-muted-foreground">{unitLabel(option.quantityBehavior)}</span>
                </div>
                {error && (
                  <p id={`${inputId}-error`} className="max-w-56 text-xs text-destructive">
                    {error}
                  </p>
                )}
              </div>
              <div className="pt-2 text-right">
                {quote?.lineTotal != null ? (
                  <p className="font-semibold tabular-nums text-foreground">{formatMoney(quote.lineTotal)}</p>
                ) : awaitingQuote && !quote && !error ? (
                  <p className="text-sm text-muted-foreground">{t('staffOrder.lines.pricing')}</p>
                ) : null}
              </div>
            </div>
          </li>
        )
      })}
    </ul>
  )
}

function LinePrice({ quote }: { quote: StaffOrderQuoteLine | undefined }) {
  const { t } = useTranslation('orders')
  if (!quote) return null
  if (quote.status === 'no-effective-price') {
    return <p className="mt-1 text-sm font-medium text-destructive">{t('staffOrder.lines.unpriced')}</p>
  }
  return (
    <div className="mt-1 flex flex-col text-sm">
      {quote.unitNetPrice != null && (
        <span className="tabular-nums text-foreground">{t('staffOrder.lines.unitPrice', { price: formatMoney(quote.unitNetPrice) })}</span>
      )}
      {quote.fellBack ? (
        <span className="text-amber-700 dark:text-amber-400">{t('staffOrder.lines.fellBack', { name: quote.priceListName })}</span>
      ) : (
        quote.priceListName && <span className="text-muted-foreground">{t('staffOrder.lines.priceList', { name: quote.priceListName })}</span>
      )}
    </div>
  )
}
