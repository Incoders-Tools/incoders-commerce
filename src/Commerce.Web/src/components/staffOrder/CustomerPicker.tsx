import { useId, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { searchStaffCustomers } from '@/api/staffOrders'
import type { StaffCustomerOption } from '@/api/types'
import { cn } from '@/lib/utils'
import { useServerSearch } from './useServerSearch'

const percentFormatter = new Intl.NumberFormat('es-AR', { maximumFractionDigits: 2 })

/** Step 1 of the take order screen: search a customer (name, tax id, phone) or show the chosen one. */
export function CustomerPicker({
  selected,
  onSelect,
}: {
  selected: StaffCustomerOption | null
  onSelect: (customer: StaffCustomerOption | null) => void
}) {
  const { t } = useTranslation('orders')
  const headingId = useId()
  const [text, setText] = useState('')
  const search = useServerSearch(selected ? '' : text, searchStaffCustomers)

  const priceListOf = (customer: StaffCustomerOption) =>
    customer.priceListName
      ? t('staffOrder.customer.priceList', { name: customer.priceListName })
      : t('staffOrder.customer.defaultPriceList')

  return (
    <section aria-labelledby={headingId} className="flex flex-col gap-3">
      <h2 id={headingId} className="text-base font-semibold text-foreground">
        {t('staffOrder.customer.title')}
      </h2>

      {selected ? (
        <div className="flex items-start justify-between gap-3 rounded-lg border border-border bg-card p-4">
          <div className="min-w-0">
            <p className="truncate font-medium text-foreground">{selected.displayName}</p>
            <p className="text-sm text-muted-foreground">
              {[selected.cityName ?? t('staffOrder.customer.noCity'), selected.taxId, selected.phone].filter(Boolean).join(' · ')}
            </p>
            <p className="mt-1 text-sm text-foreground">
              {priceListOf(selected)}
              {selected.discountPercentage ? (
                <span className="text-muted-foreground">
                  {' · '}
                  {t('staffOrder.customer.discount', { discount: percentFormatter.format(selected.discountPercentage) })}
                </span>
              ) : null}
            </p>
          </div>
          <Button
            variant="outline"
            className="h-11 shrink-0 lg:h-9"
            onClick={() => {
              setText('')
              onSelect(null)
            }}
          >
            {t('staffOrder.customer.change')}
          </Button>
        </div>
      ) : (
        <div className="flex flex-col gap-2">
          <Label htmlFor={`${headingId}-search`}>{t('staffOrder.customer.searchLabel')}</Label>
          <Input
            id={`${headingId}-search`}
            type="search"
            autoComplete="off"
            className="h-11 text-base lg:h-9 lg:text-sm"
            placeholder={t('staffOrder.customer.searchPlaceholder')}
            value={text}
            onChange={(event) => setText(event.target.value)}
          />
          <SearchFeedback status={search.status} empty={search.results.length === 0} hint={t('staffOrder.customer.hint')}
            searching={t('staffOrder.customer.searching')} noResults={t('staffOrder.customer.noResults')}
            error={t('staffOrder.customer.searchError')} />
          {search.results.length > 0 && (
            <ul className="flex flex-col gap-2">
              {search.results.map((customer) => (
                <li key={customer.id}>
                  <button
                    type="button"
                    disabled={!customer.isEnabled}
                    onClick={() => onSelect(customer)}
                    className={cn(
                      'flex min-h-14 w-full flex-col items-start gap-0.5 rounded-lg border border-border bg-card px-4 py-3 text-left transition-colors',
                      customer.isEnabled ? 'hover:bg-accent hover:text-accent-foreground' : 'cursor-not-allowed opacity-60',
                    )}
                  >
                    <span className="font-medium text-foreground">{customer.displayName}</span>
                    <span className="text-sm text-muted-foreground">
                      {[customer.cityName ?? t('staffOrder.customer.noCity'), priceListOf(customer)].join(' · ')}
                    </span>
                    {!customer.isEnabled && (
                      <span className="text-xs font-medium text-destructive">{t('staffOrder.customer.disabled')}</span>
                    )}
                  </button>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
    </section>
  )
}

/** The one-line state under a picker's search box: hint, searching, no match or failure. */
export function SearchFeedback({
  status,
  empty,
  hint,
  searching,
  noResults,
  error,
}: {
  status: 'idle' | 'loading' | 'ready' | 'error'
  empty: boolean
  hint?: string
  searching: string
  noResults: string
  error: string
}) {
  const message =
    status === 'error'
      ? error
      : status === 'loading' && empty
        ? searching
        : status === 'ready' && empty
          ? noResults
          : status === 'idle'
            ? hint
            : undefined
  if (!message) return null
  return <p className={cn('text-sm', status === 'error' ? 'text-destructive' : 'text-muted-foreground')}>{message}</p>
}
