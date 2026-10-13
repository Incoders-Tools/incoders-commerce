import * as React from 'react'
import { cn } from '@/lib/utils'
import { DateInput } from './DateInput'

export const Input = React.forwardRef<HTMLInputElement, React.InputHTMLAttributes<HTMLInputElement>>(
  ({ className, type, ...props }, ref) =>
    // owner feedback P3: every date reads and is typed dd/mm/aaaa, whatever the browser's language.
    type === 'date' ? (
      <DateInput ref={ref} className={className} {...(props as React.ComponentProps<typeof DateInput>)} />
    ) : (
      <input
        ref={ref}
        type={type}
        className={cn(
          'flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm text-foreground shadow-sm transition-colors placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50',
          className,
        )}
        {...props}
      />
    ),
)
Input.displayName = 'Input'
