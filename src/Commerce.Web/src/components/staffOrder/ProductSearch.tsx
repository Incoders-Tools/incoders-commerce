import { useId, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Plus } from 'lucide-react'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { searchStaffPresentations } from '@/api/staffOrders'
import type { StaffPresentationOption } from '@/api/types'
import { SearchFeedback } from './CustomerPicker'
import { presentationName } from './presentationName'
import { useServerSearch } from './useServerSearch'

/** Step 2 of the take order screen: search active products by name or code and add one as a line. */
export function ProductSearch({ onAdd }: { onAdd: (option: StaffPresentationOption) => void }) {
  const { t } = useTranslation('orders')
  const id = useId()
  const [text, setText] = useState('')
  const search = useServerSearch(text, searchStaffPresentations)

  return (
    <div className="flex flex-col gap-2">
      <Label htmlFor={`${id}-search`}>{t('staffOrder.products.searchLabel')}</Label>
      <Input
        id={`${id}-search`}
        type="search"
        autoComplete="off"
        className="h-11 text-base lg:h-9 lg:text-sm"
        placeholder={t('staffOrder.products.searchPlaceholder')}
        value={text}
        onChange={(event) => setText(event.target.value)}
      />
      <SearchFeedback status={search.status} empty={search.results.length === 0}
        searching={t('staffOrder.products.searching')} noResults={t('staffOrder.products.noResults')}
        error={t('staffOrder.products.searchError')} />
      {search.results.length > 0 && (
        <ul className="flex flex-col gap-2">
          {search.results.map((option) => (
            <li key={option.presentationId}>
              <button
                type="button"
                aria-label={t('staffOrder.products.add', { name: presentationName(option) })}
                onClick={() => {
                  onAdd(option)
                  setText('')
                }}
                className="flex min-h-14 w-full items-center justify-between gap-3 rounded-lg border border-border bg-card px-4 py-3 text-left transition-colors hover:bg-accent hover:text-accent-foreground"
              >
                <span className="flex min-w-0 flex-col">
                  <span className="font-medium text-foreground">{option.productName}</span>
                  <span className="text-sm text-muted-foreground">
                    {[option.presentationName, option.identificationCode].filter(Boolean).join(' · ')}
                  </span>
                </span>
                <span className="flex shrink-0 items-center gap-1 text-sm font-medium text-primary">
                  <Plus aria-hidden="true" className="size-4" />
                  {t('staffOrder.products.addShort')}
                </span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
