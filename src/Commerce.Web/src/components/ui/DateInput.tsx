import * as React from 'react'
import { useTranslation } from 'react-i18next'
import { CalendarDays } from 'lucide-react'
import { cn } from '@/lib/utils'

const ISO = /^(\d{4})-(\d{2})-(\d{2})$/

/** "2026-10-13" when the three parts make a real day; null otherwise. */
function toIso(day: string, month: string, year: string): string | null {
  const [d, m, y] = [Number(day), Number(month), Number(year)]
  const date = new Date(Date.UTC(y, m - 1, d))
  const real = y >= 1900 && date.getUTCFullYear() === y && date.getUTCMonth() === m - 1 && date.getUTCDate() === d
  return real ? `${year}-${month}-${day}` : null
}

/** "dd/mm/aaaa" text as an ISO date, or '' while incomplete or not a real day. */
function parse(text: string): string {
  const match = /^(\d{2})\/(\d{2})\/(\d{4})$/.exec(text)
  return match ? (toIso(match[1], match[2], match[3]) ?? '') : ''
}

const display = (iso: string | undefined) => {
  const match = ISO.exec(iso ?? '')
  return match ? `${match[3]}/${match[2]}/${match[1]}` : ''
}

/** The digits typed, with the slashes put as each part completes: "1310" reads "13/10/". */
function withSlashes(raw: string): string {
  const digits = raw.replace(/\D/g, '').slice(0, 8)
  const day = digits.slice(0, 2)
  const month = digits.slice(2, 4)
  const year = digits.slice(4)
  if (digits.length < 2) return digits
  if (digits.length < 4) return `${day}/${month}`
  return `${day}/${month}/${year}`
}

type DateInputProps = Omit<React.InputHTMLAttributes<HTMLInputElement>, 'type' | 'value' | 'onChange'> & {
  /** ISO date ("2026-10-13") or ''. */
  value?: string
  /** Called like the browser's date field: `event.target.value` is the ISO date, or '' while incomplete. */
  onChange?: (event: React.ChangeEvent<HTMLInputElement>) => void
}

/**
 * owner feedback P3: a date field that always reads and takes dd/mm/aaaa (the browser's own date field follows the
 * browser's language: mm/dd/yyyy in an English one). The screens keep handing it and getting ISO dates; a calendar
 * button opens the browser's picker.
 */
export const DateInput = React.forwardRef<HTMLInputElement, DateInputProps>(
  ({ className, value, onChange, min, max, disabled, ...props }, ref) => {
    const { t } = useTranslation('common')
    const [text, setText] = React.useState(() => display(value))
    const picker = React.useRef<HTMLInputElement>(null)
    const field = React.useRef<HTMLInputElement | null>(null)
    const setField = (node: HTMLInputElement | null) => {
      field.current = node
      if (typeof ref === 'function') ref(node)
      else if (ref) ref.current = node
    }
    // The value last seen from the screen, and the last one this field gave it.
    const [seen, setSeen] = React.useState(value ?? '')
    const [given, setGiven] = React.useState<string | null>(null)

    // What is typed stays (half a date, a day that does not exist, a field the screen does not hold). A date the screen
    // sets by itself (loaded, cleared, proposed) replaces it at once, in the same render, as the browser's field would;
    // the screen taking back what this field just gave it changes nothing.
    const current = value ?? ''
    if (current !== seen) {
      setSeen(current)
      if (current !== given) setText(display(current))
      // Seen once: the next time the screen holds this value, it is the screen's own.
      setGiven(null)
    }

    const emit = (iso: string) => {
      setGiven(iso)
      onChange?.({ target: { value: iso }, currentTarget: { value: iso } } as React.ChangeEvent<HTMLInputElement>)
    }

    const change = (raw: string) => {
      if (ISO.test(raw)) {
        const [, year, month, day] = ISO.exec(raw)!
        setText(`${day}/${month}/${year}`)
        return emit(toIso(day, month, year) ?? '')
      }
      // Erasing keeps what is left as it is (a slash can be erased); typing puts the slashes.
      const next = raw.length < text.length ? raw : withSlashes(raw)
      setText(next)
      emit(parse(next))
    }

    // A date typed outside min/max is invalid, as in the browser's own date field: the form does not send it.
    const typed = parse(text)
    const outOfRange =
      typed !== '' && min && typed < String(min)
        ? t('dateInput.min', { date: display(String(min)) })
        : typed !== '' && max && typed > String(max)
          ? t('dateInput.max', { date: display(String(max)) })
          : ''
    React.useEffect(() => {
      field.current?.setCustomValidity(outOfRange)
    }, [outOfRange])

    return (
      <div className={cn('relative flex w-full items-center', className)}>
        <input
          ref={setField}
          aria-invalid={outOfRange ? true : undefined}
          type="text"
          inputMode="numeric"
          autoComplete="off"
          maxLength={10}
          placeholder={t('dateInput.placeholder')}
          min={min}
          max={max}
          disabled={disabled}
          value={text}
          onChange={(e) => change(e.target.value)}
          className="flex h-9 w-full rounded-md border border-input bg-background py-1 pr-9 pl-3 text-sm text-foreground shadow-sm transition-colors placeholder:text-muted-foreground focus-visible:ring-1 focus-visible:ring-ring focus-visible:outline-none disabled:cursor-not-allowed disabled:opacity-50 aria-invalid:border-destructive"
          {...props}
        />
        <button
          type="button"
          aria-label={t('dateInput.pick')}
          disabled={disabled}
          onClick={() => picker.current?.showPicker?.()}
          className="absolute right-1 rounded p-1 text-muted-foreground hover:text-foreground disabled:opacity-50"
        >
          <CalendarDays aria-hidden="true" className="size-4" />
        </button>
        <input
          ref={picker}
          type="date"
          tabIndex={-1}
          aria-hidden="true"
          className="pointer-events-none absolute right-0 bottom-0 size-0 opacity-0"
          min={min}
          max={max}
          value={value ?? ''}
          onChange={(e) => {
            setText(display(e.target.value))
            emit(e.target.value)
          }}
        />
      </div>
    )
  },
)
DateInput.displayName = 'DateInput'
