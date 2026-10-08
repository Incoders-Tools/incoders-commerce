import { useId, type ReactNode } from 'react'
import { Card } from '@/components/ui/card'
import { cn } from '@/lib/utils'

/** Titled widget surface: a labelled region so the dashboard is navigable by landmark. */
export function DashboardCard({
  title,
  actions,
  className,
  children,
}: {
  title: string
  actions?: ReactNode
  className?: string
  children: ReactNode
}) {
  const titleId = useId()
  return (
    <Card role="region" aria-labelledby={titleId} className={cn('flex min-w-0 flex-col gap-3 p-4 md:p-5', className)}>
      <div className="flex items-start justify-between gap-2">
        <h2 id={titleId} className="text-sm font-semibold text-foreground">
          {title}
        </h2>
        {actions}
      </div>
      {children}
    </Card>
  )
}
