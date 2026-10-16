import { useEffect, useId, useMemo, useRef, useState, type KeyboardEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { X } from 'lucide-react'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { cn } from '@/lib/utils'
import { presentationLabel, type PresentationOption } from './presentationOptions'

const RESULT_LIMIT = 30

const normalize = (text: string) =>
  text
    .normalize('NFD')
    .replace(/[̀-ͯ]/g, '')
    .toLowerCase()

interface PresentationPickerProps {
  label: string
  options: PresentationOption[]
  value: PresentationOption | null
  onChange: (option: PresentationOption | null) => void
  id?: string
  placeholder?: string
  error?: string | null
  loadFailed?: boolean
  className?: string
}

/**
 * Accessible combobox (ARIA 1.2 list-box popup, same shape as `CityPicker`)
 * over the branch catalog the screen already loaded: the typed text filters
 * product, presentation and identification code, accent-insensitively.
 */
export function PresentationPicker({
  label,
  options,
  value,
  onChange,
  id,
  placeholder,
  error = null,
  loadFailed = false,
  className,
}: PresentationPickerProps) {
  const { t } = useTranslation('purchases')
  const generatedId = useId()
  const inputId = id ?? `presentation-picker-${generatedId}`
  const listboxId = `${inputId}-listbox`
  const [query, setQuery] = useState('')
  const [open, setOpen] = useState(false)
  const [activeIndex, setActiveIndex] = useState(0)
  const rootRef = useRef<HTMLDivElement>(null)

  const results = useMemo(() => {
    const needle = normalize(query.trim())
    const matches =
      needle === ''
        ? options
        : options.filter((option) =>
            normalize(`${presentationLabel(option)} ${option.identificationCode ?? ''}`).includes(needle),
          )
    return matches.slice(0, RESULT_LIMIT)
  }, [options, query])

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
    setActiveIndex(0)
  }

  const choose = (option: PresentationOption) => {
    onChange(option)
    close()
  }

  const handleKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'ArrowDown') {
      event.preventDefault()
      if (!open) setOpen(true)
      else setActiveIndex((index) => Math.min(index + 1, results.length - 1))
    } else if (event.key === 'ArrowUp') {
      event.preventDefault()
      setActiveIndex((index) => Math.max(index - 1, 0))
    } else if (event.key === 'Enter' && open && results[activeIndex]) {
      event.preventDefault()
      choose(results[activeIndex])
    } else if (event.key === 'Escape' && open) {
      event.preventDefault()
      close()
    }
  }

  const activeOptionId = open && results[activeIndex] ? `${inputId}-option-${activeIndex}` : undefined
  const errorId = `${inputId}-error`

  return (
    <div ref={rootRef} className={cn('relative flex flex-col gap-1.5', className)}>
      <Label htmlFor={inputId}>{label}</Label>
      <div className="relative">
        <Input
          id={inputId}
          role="combobox"
          autoComplete="off"
          aria-expanded={open}
          aria-controls={listboxId}
          aria-autocomplete="list"
          aria-activedescendant={activeOptionId}
          aria-invalid={error ? true : undefined}
          aria-describedby={error ? errorId : undefined}
          placeholder={placeholder}
          value={open ? query : value ? presentationLabel(value) : ''}
          className={value ? 'pr-9' : undefined}
          onFocus={() => setOpen(true)}
          onClick={() => setOpen(true)}
          onChange={(event) => {
            setQuery(event.target.value)
            setActiveIndex(0)
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
      {error && (
        <p id={errorId} className="text-xs text-destructive">
          {error}
        </p>
      )}
      {open && (
        <div className="absolute left-0 right-0 top-full z-20 mt-1 max-h-64 overflow-y-auto rounded-md border border-border bg-card text-sm shadow-md">
          <ul id={listboxId} role="listbox" aria-label={label}>
            {results.map((option, index) => (
              <li
                key={option.id}
                id={`${inputId}-option-${index}`}
                role="option"
                aria-selected={index === activeIndex}
                className={cn('cursor-pointer px-3 py-2', index === activeIndex && 'bg-accent text-accent-foreground')}
                onMouseEnter={() => setActiveIndex(index)}
                // mousedown + preventDefault keeps focus in the input, so the click is not lost to a blur.
                onMouseDown={(event) => event.preventDefault()}
                onClick={() => choose(option)}
              >
                {presentationLabel(option)}
                {option.identificationCode && (
                  <span className="ml-2 text-xs text-muted-foreground">{option.identificationCode}</span>
                )}
              </li>
            ))}
          </ul>
          {loadFailed && <p className="px-3 py-2 text-destructive">{t('picker.loadError')}</p>}
          {!loadFailed && results.length === 0 && (
            <p className="px-3 py-2 text-muted-foreground">
              {options.length === 0 ? t('picker.searching') : t('picker.noResults')}
            </p>
          )}
        </div>
      )}
    </div>
  )
}
