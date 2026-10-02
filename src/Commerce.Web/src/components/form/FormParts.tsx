import type { ReactNode } from 'react'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select } from '@/components/ui/select'
import { cn } from '@/lib/utils'

/**
 * Building blocks shared by the full-page entity forms (customers,
 * suppliers): a titled responsive section, a labelled text field and a
 * catalog select. Purely presentational.
 */
export function FormSection({ title, children, wide = false }: { title: string; children: ReactNode; wide?: boolean }) {
  return (
    <fieldset className="flex flex-col gap-4">
      <legend className="mb-1 text-sm font-semibold text-foreground">{title}</legend>
      <div
        className={cn(
          'grid grid-cols-1 gap-x-6 gap-y-4',
          // `wide` sections hold one full-width control (notes) on every breakpoint.
          !wide && 'md:grid-cols-2 xl:grid-cols-3',
        )}
      >
        {children}
      </div>
    </fieldset>
  )
}

export function Field({
  id,
  label,
  value,
  onChange,
  type = 'text',
  required = false,
  error = null,
  hint,
  className,
}: {
  id: string
  label: string
  value: string
  onChange: (value: string) => void
  type?: string
  required?: boolean
  error?: string | null
  hint?: string
  className?: string
}) {
  const describedBy = error ? `${id}-error` : hint ? `${id}-hint` : undefined
  return (
    <div className={cn('flex flex-col gap-1.5', className)}>
      <Label htmlFor={id}>{label}</Label>
      <Input
        id={id}
        type={type}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        required={required}
        aria-invalid={error ? true : undefined}
        aria-describedby={describedBy}
      />
      {error ? (
        <p id={`${id}-error`} className="text-xs text-destructive">
          {error}
        </p>
      ) : (
        hint && (
          <p id={`${id}-hint`} className="text-xs text-muted-foreground">
            {hint}
          </p>
        )
      )}
    </div>
  )
}

export function CatalogSelect({
  id,
  label,
  emptyLabel,
  value,
  onChange,
  options,
}: {
  id: string
  label: string
  emptyLabel: string
  value: string
  onChange: (value: string) => void
  options: { id: string; name: string }[]
}) {
  return (
    <div className="flex flex-col gap-1.5">
      <Label htmlFor={id}>{label}</Label>
      <Select id={id} value={value} onChange={(e) => onChange(e.target.value)}>
        <option value="">{emptyLabel}</option>
        {options.map((option) => (
          <option key={option.id} value={option.id}>
            {option.name}
          </option>
        ))}
      </Select>
    </div>
  )
}
