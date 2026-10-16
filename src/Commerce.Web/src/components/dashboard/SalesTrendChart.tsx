import { useState } from 'react'
import { CartesianGrid, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { formatArs, formatArsCompact, formatBucket } from '@/dashboard/format'
import type { SalesSeries } from '@/dashboard/types'
import { DashboardCard } from './DashboardCard'
import { channelMeta, channelOrder } from './channels'

const AXIS_TICK = { fill: 'var(--muted-foreground)', fontSize: 12 }

interface TooltipEntry {
  dataKey?: string | number
  value?: number | string
}

function TrendTooltip({
  active,
  payload,
  label,
}: {
  active?: boolean
  payload?: ReadonlyArray<TooltipEntry>
  label?: string | number
}) {
  const { t } = useTranslation('dashboard')
  if (!active || !payload?.length) return null
  return (
    <div className="rounded-md border border-border bg-card px-3 py-2 text-xs shadow-md">
      <p className="mb-1 font-medium text-foreground">{formatBucket(String(label))}</p>
      <ul className="flex flex-col gap-1">
        {channelOrder.map((channel) => {
          const entry = payload.find((item) => item.dataKey === channel)
          if (!entry) return null
          return (
            <li key={channel} className="flex items-center justify-between gap-4">
              <span className="flex items-center gap-2 text-muted-foreground">
                <span
                  aria-hidden="true"
                  className="size-2 rounded-full"
                  style={{ backgroundColor: channelMeta[channel].color }}
                />
                {t(channelMeta[channel].labelKey)}
              </span>
              <span className="text-foreground">{formatArs(Number(entry.value))}</span>
            </li>
          )
        })}
      </ul>
    </div>
  )
}

/**
 * Daily (or, for "today", hourly) sales per channel as one thin line each,
 * on a single money axis. The legend is always shown and a table view is one
 * click away, so identity never depends on colour alone (the aqua slot is
 * below 3:1 on the light surface; the table is its relief).
 */
export function SalesTrendChart({ series, className }: { series: SalesSeries; className?: string }) {
  const { t } = useTranslation('dashboard')
  const [asTable, setAsTable] = useState(false)
  const title = t('trend.title')
  const hasData = series.points.length > 0

  return (
    <DashboardCard
      title={title}
      className={className}
      actions={
        hasData ? (
          <Button type="button" variant="outline" size="sm" onClick={() => setAsTable((prev) => !prev)}>
            {asTable ? t('trend.viewChart') : t('trend.viewTable')}
          </Button>
        ) : undefined
      }
    >
      {!hasData ? (
        <p data-empty="trend" className="text-sm text-muted-foreground">
          {t('trend.empty')}
        </p>
      ) : (
        <>
          <ul className="flex flex-wrap gap-x-4 gap-y-1">
            {channelOrder.map((channel) => (
              <li key={channel} className="flex items-center gap-2 text-xs text-muted-foreground">
                <span
                  aria-hidden="true"
                  className="h-0.5 w-4 rounded-full"
                  style={{ backgroundColor: channelMeta[channel].color }}
                />
                {t(channelMeta[channel].labelKey)}
              </li>
            ))}
          </ul>
          {asTable ? (
            <div className="max-h-72 overflow-auto">
              <table aria-label={title} className="w-full text-sm">
                <thead>
                  <tr className="text-left text-xs text-muted-foreground">
                    <th scope="col" className="pb-2 font-medium">
                      {t('trend.period')}
                    </th>
                    {channelOrder.map((channel) => (
                      <th key={channel} scope="col" className="pb-2 text-right font-medium">
                        {t(channelMeta[channel].labelKey)}
                      </th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {series.points.map((point) => (
                    <tr key={point.bucket} className="border-t border-border">
                      <th scope="row" className="py-1.5 text-left font-normal text-muted-foreground">
                        {formatBucket(point.bucket)}
                      </th>
                      {channelOrder.map((channel) => (
                        <td key={channel} className="py-1.5 text-right tabular-nums text-foreground">
                          {formatArs(point[channel])}
                        </td>
                      ))}
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ) : (
            <div className="h-64 w-full min-w-0 md:h-72">
              <ResponsiveContainer width="100%" height="100%" minWidth={0}>
                <LineChart data={series.points} margin={{ top: 8, right: 8, bottom: 0, left: 0 }}>
                  <CartesianGrid vertical={false} stroke="var(--border)" strokeWidth={1} />
                  <XAxis
                    dataKey="bucket"
                    tickFormatter={formatBucket}
                    tick={AXIS_TICK}
                    tickLine={false}
                    axisLine={{ stroke: 'var(--border)' }}
                    minTickGap={16}
                  />
                  <YAxis
                    tickFormatter={formatArsCompact}
                    tick={AXIS_TICK}
                    tickLine={false}
                    axisLine={false}
                    width={64}
                  />
                  <Tooltip content={<TrendTooltip />} cursor={{ stroke: 'var(--border)' }} />
                  {channelOrder.map((channel) => (
                    <Line
                      key={channel}
                      type="monotone"
                      dataKey={channel}
                      stroke={channelMeta[channel].color}
                      strokeWidth={2}
                      strokeLinecap="round"
                      strokeLinejoin="round"
                      dot={false}
                      activeDot={{ r: 4, stroke: 'var(--card)', strokeWidth: 2 }}
                      isAnimationActive={false}
                    />
                  ))}
                </LineChart>
              </ResponsiveContainer>
            </div>
          )}
        </>
      )}
    </DashboardCard>
  )
}
