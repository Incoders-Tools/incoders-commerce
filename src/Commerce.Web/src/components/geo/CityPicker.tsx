import { useEffect, useId, useRef, useState, type KeyboardEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { X } from 'lucide-react'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { listCities } from '@/api/geo'
import type { GeoCity } from '@/api/types'
import { useDebouncedValue } from '@/lib/useDebouncedValue'
import { cn } from '@/lib/utils'
import { cityLabel } from './cityLabel'

export type { CityOption } from './cityLabel'
import type { CityOption } from './cityLabel'

const SEARCH_DEBOUNCE_MS = 250
const RESULT_LIMIT = 20

interface CityPickerProps {
  label: string
  value: CityOption | null
  onChange: (city: CityOption | null) => void
  id?: string
  placeholder?: string
  /** Visually hide the label (a toolbar filter that is named by its placeholder). */
  hideLabel?: boolean
  className?: string
}

/**
 * Accessible combobox (ARIA 1.2 list-box popup) over `GET /geo/cities`.
 * The catalog has ~4000 entries, so it is never loaded whole: the server
 * searches while the user types. The current value is rendered from the
 * `value` the parent already holds (e.g. a customer's cityName/provinceName),
 * so showing it costs no request.
 */
export function CityPicker({ label, value, onChange, id, placeholder, hideLabel = false, className }: CityPickerProps) {
  const { t } = useTranslation('cities')
  const generatedId = useId()
  const inputId = id ?? `city-picker-${generatedId}`
  const listboxId = `${inputId}-listbox`
  const [query, setQuery] = useState('')
  const [open, setOpen] = useState(false)
  const [results, setResults] = useState<GeoCity[] | null>(null)
  const [failed, setFailed] = useState(false)
  const [activeIndex, setActiveIndex] = useState(0)
  const rootRef = useRef<HTMLDivElement>(null)
  const debouncedQuery = useDebouncedValue(query.trim(), SEARCH_DEBOUNCE_MS)

  useEffect(() => {
    if (!open) return
    let current = true
    listCities({ search: debouncedQuery, limit: RESULT_LIMIT }).then(
      (cities) => {
        if (!current) return
        setResults(cities)
        setFailed(false)
        setActiveIndex(0)
      },
      () => {
        if (current) setFailed(true)
      },
    )
    return () => {
      current = false
    }
  }, [open, debouncedQuery])

  useEffect(() => {
    if (!open) return
    const closeOnOutsideClick = (event: MouseEvent) => {
      if (rootRef.current && !rootRef.current.contains(event.target as Node)) setOpen(false)
    }
    document.addEventListener('mousedown', closeOnOutsideClick)
    return () => document.removeEventListener('mousedown', closeOnOutsideClick)
  }, [open])

  const close = () => {
    setOpen(false)
    setQuery('')
    setResults(null)
  }

  const choose = (city: GeoCity) => {
    onChange({ id: city.id, name: city.name, provinceName: city.provinceName })
    close()
  }

  const handleKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    const count = results?.length ?? 0
    if (event.key === 'ArrowDown') {
      event.preventDefault()
      if (!open) setOpen(true)
      else if (count > 0) setActiveIndex((index) => Math.min(index + 1, count - 1))
    } else if (event.key === 'ArrowUp') {
      event.preventDefault()
      setActiveIndex((index) => Math.max(index - 1, 0))
    } else if (event.key === 'Enter' && open && results && results[activeIndex]) {
      event.preventDefault()
      choose(results[activeIndex])
    } else if (event.key === 'Escape' && open) {
      event.preventDefault()
      close()
    }
  }

  const activeOptionId = open && results?.[activeIndex] ? `${inputId}-option-${activeIndex}` : undefined

  return (
    <div ref={rootRef} className={cn('relative flex flex-col gap-1.5', className)}>
      <Label htmlFor={inputId} className={hideLabel ? 'sr-only' : undefined}>
        {label}
      </Label>
      <div className="relative">
        <Input
          id={inputId}
          role="combobox"
          autoComplete="off"
          aria-expanded={open}
          aria-controls={listboxId}
          aria-autocomplete="list"
          aria-activedescendant={activeOptionId}
          placeholder={placeholder}
          value={open ? query : value ? cityLabel(value) : ''}
          className={value ? 'pr-9' : undefined}
          onFocus={() => setOpen(true)}
          onChange={(event) => {
            setQuery(event.target.value)
            setOpen(true)
          }}
          onKeyDown={handleKeyDown}
        />
        {value && (
          <button
            type="button"
            aria-label={t('picker.clear')}
            className="absolute inset-y-0 right-0 flex w-9 items-center justify-center text-muted-foreground hover:text-foreground"
            onClick={() => onChange(null)}
          >
            <X aria-hidden="true" className="size-4" />
          </button>
        )}
      </div>
      {open && (
        <div className="absolute left-0 right-0 top-full z-20 mt-1 max-h-64 overflow-y-auto rounded-md border border-border bg-card text-sm shadow-md">
          <ul id={listboxId} role="listbox" aria-label={label}>
            {(results ?? []).map((city, index) => (
              <li
                key={city.id}
                id={`${inputId}-option-${index}`}
                role="option"
                aria-selected={index === activeIndex}
                className={cn('cursor-pointer px-3 py-2', index === activeIndex && 'bg-accent text-accent-foreground')}
                onMouseEnter={() => setActiveIndex(index)}
                // mousedown + preventDefault keeps focus in the input, so the click is not lost to a blur.
                onMouseDown={(event) => event.preventDefault()}
                onClick={() => choose(city)}
              >
                {cityLabel(city)}
              </li>
            ))}
          </ul>
          {failed && <p className="px-3 py-2 text-destructive">{t('picker.loadError')}</p>}
          {!failed && results === null && <p className="px-3 py-2 text-muted-foreground">{t('picker.searching')}</p>}
          {!failed && results?.length === 0 && <p className="px-3 py-2 text-muted-foreground">{t('picker.noResults')}</p>}
        </div>
      )}
    </div>
  )
}
