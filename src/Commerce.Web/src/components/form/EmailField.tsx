import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { CircleCheck } from 'lucide-react'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { canBecomeValidEmail, emailStatus } from '@/lib/email'
import { useDebouncedValue } from '@/lib/useDebouncedValue'
import { cn } from '@/lib/utils'

/** How long the value must stay unchanged before an address that is still incomplete is flagged. */
const PAUSE_MS = 1500

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
 * rule (`lib/email.ts`) while typing. A valid address gets a green check at once; an invalid one is flagged only
 * when it can no longer become valid, on blur, or once the user stops typing, so finishing an address is never
 * nagged. Blank is neutral: whether the field is required is the form's business.
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
  const [blurred, setBlurred] = useState(false)
  const settled = useDebouncedValue(value, PAUSE_MS) === value
  const status = emailStatus(value)
  const ownError = status === 'invalid' && (blurred || settled || !canBecomeValidEmail(value))
  const message = error ?? (ownError ? t('email.invalid') : null)
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
          onBlur={() => setBlurred(true)}
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
