import { useTranslation } from 'react-i18next'
import { CircleCheck } from 'lucide-react'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { emailStatus } from '@/lib/email'
import { cn } from '@/lib/utils'

interface EmailFieldProps {
  id: string
  label: string
  value: string
  onChange: (value: string) => void
  required?: boolean
  /** Visually hide the label (a compact form named by its placeholder); it still names the input. */
  hideLabel?: boolean
  placeholder?: string
  /** An error the form owns (a refused submit or a 400 from the server); it wins over the field's own state. */
  error?: string | null
  className?: string
}

/**
 * The one email input every email field uses (customer, contact, staff user, supplier), checked with the shared
 * rule (`lib/email.ts`) on every keystroke (owner decision 2026-10-03): flagged as invalid until the address is
 * valid, and only then the green check. Blank is neutral: whether the field is required is the form's business.
 */
export function EmailField({
  id,
  label,
  value,
  onChange,
  required = false,
  hideLabel = false,
  placeholder,
  error = null,
  className,
}: EmailFieldProps) {
  const { t } = useTranslation('common')
  const status = emailStatus(value)
  const message = error ?? (status === 'invalid' ? t('email.invalid') : null)
  const valid = message === null && status === 'valid'

  return (
    <div className={cn('flex flex-col gap-1.5', className)}>
      <Label htmlFor={id} className={hideLabel ? 'sr-only' : undefined}>
        {label}
      </Label>
      <div className="relative">
        <Input
          id={id}
          type="email"
          autoComplete="email"
          value={value}
          placeholder={placeholder}
          required={required}
          className="pr-9"
          aria-invalid={message ? true : undefined}
          aria-describedby={message ? `${id}-error` : valid ? `${id}-valid` : undefined}
          onChange={(e) => onChange(e.target.value)}
        />
        {valid && (
          <CircleCheck
            aria-hidden="true"
            data-testid="email-valid-icon"
            className="pointer-events-none absolute inset-y-0 right-2.5 my-auto size-4 text-emerald-600 dark:text-emerald-400"
          />
        )}
      </div>
      {message && (
        <p id={`${id}-error`} className="text-xs text-destructive">
          {message}
        </p>
      )}
      {valid && (
        <span id={`${id}-valid`} className="sr-only">
          {t('email.valid')}
        </span>
      )}
    </div>
  )
}
