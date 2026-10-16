import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { dashboardPeriods, type DashboardPeriod } from '@/dashboard/types'

export function PeriodSelector({
  value,
  onChange,
}: {
  value: DashboardPeriod
  onChange: (period: DashboardPeriod) => void
}) {
  const { t } = useTranslation('dashboard')
  return (
    <div role="group" aria-label={t('period.label')} className="inline-flex gap-1 rounded-lg bg-muted p-1">
      {dashboardPeriods.map((period) => (
        <Button
          key={period}
          type="button"
          size="sm"
          variant={period === value ? 'default' : 'outline'}
          aria-pressed={period === value}
          className={period === value ? undefined : 'border-transparent bg-transparent'}
          onClick={() => onChange(period)}
        >
          {t(`period.${period}`)}
        </Button>
      ))}
    </div>
  )
}
