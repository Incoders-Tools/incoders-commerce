import type { InputHTMLAttributes } from 'react'
import { useTranslation } from 'react-i18next'
import { Field } from '@/components/form/FormParts'
import { Input } from '@/components/ui/input'
import { formatMoney, readMoney } from '@/lib/money'
import { cn } from '@/lib/utils'

/**
 * What a money field read from its text, under the field: "Se toma como $ 15.000,00". Nothing while it is blank or
 * unreadable (the form says what is wrong when it validates). Shown by every money field, so `15.000` is never taken
 * for 15 without the operator seeing it.
 */
export function MoneyReadBack({ text, maxDecimals = 2, className }: { text: string; maxDecimals?: number; className?: string }) {
  const { t } = useTranslation('common')
  const reading = readMoney(text, maxDecimals)
  if (!reading.ok) return null
  return (
    <p className={cn('text-xs text-muted-foreground', className)} aria-live="polite">
      {t('money.readAs', { amount: formatMoney(reading.value, maxDecimals) })}
    </p>
  )
}

type MoneyInputProps = Omit<InputHTMLAttributes<HTMLInputElement>, 'value' | 'onChange' | 'inputMode' | 'placeholder' | 'type'> & {
  value: string
  onChange: (value: string) => void
  maxDecimals?: number
  /** An input inside a row (a payslip line, a price cell): the short placeholder and no read-back line under it. */
  compact?: boolean
}

/**
 * A bare money input for screens that lay out their own label: text with a decimal keyboard, the shared placeholder
 * and, unless compact, the read-back right after it (a fragment, so it stacks inside the caller's column).
 */
export function MoneyInput({ value, onChange, maxDecimals = 2, compact = false, ...rest }: MoneyInputProps) {
  const { t } = useTranslation('common')
  return (
    <>
      <Input
        {...rest}
        inputMode="decimal"
        placeholder={t(compact ? 'money.placeholderShort' : 'money.placeholder')}
        value={value}
        onChange={(e) => onChange(e.target.value)}
      />
      {!compact && <MoneyReadBack text={value} maxDecimals={maxDecimals} />}
    </>
  )
}

/** A labelled money field: text with a decimal keyboard, the shared placeholder and the read-back under it. */
export function MoneyField({
  id,
  label,
  value,
  onChange,
  maxDecimals = 2,
  required = false,
  error = null,
  hint,
  className,
}: {
  id: string
  label: string
  value: string
  onChange: (value: string) => void
  maxDecimals?: number
  required?: boolean
  error?: string | null
  hint?: string
  className?: string
}) {
  const { t } = useTranslation('common')
  return (
    <div className={cn('flex flex-col gap-1', className)}>
      <Field
        id={id}
        label={label}
        value={value}
        onChange={onChange}
        inputMode="decimal"
        placeholder={t('money.placeholder')}
        required={required}
        error={error}
        hint={hint}
      />
      <MoneyReadBack text={value} maxDecimals={maxDecimals} />
    </div>
  )
}
