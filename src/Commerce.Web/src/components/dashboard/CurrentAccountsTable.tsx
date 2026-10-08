import { useTranslation } from 'react-i18next'
import { formatArs } from '@/dashboard/format'
import type { CurrentAccount } from '@/dashboard/types'
import { DashboardCard } from './DashboardCard'

export function CurrentAccountsTable({ accounts, className }: { accounts: CurrentAccount[]; className?: string }) {
  const { t } = useTranslation('dashboard')
  const title = t('accounts.title')

  return (
    <DashboardCard title={title} className={className}>
      {accounts.length === 0 ? (
        <p className="text-sm text-muted-foreground">{t('accounts.empty')}</p>
      ) : (
        <table aria-label={title} className="w-full table-fixed text-sm">
          <thead>
            <tr className="text-left text-xs text-muted-foreground">
              <th scope="col" className="w-2/5 pb-2 font-medium">
                {t('accounts.customer')}
              </th>
              <th scope="col" className="pb-2 text-right font-medium">
                {t('accounts.balance')}
              </th>
              <th scope="col" className="w-20 pb-2 text-right font-medium">
                {t('accounts.overdue')}
              </th>
            </tr>
          </thead>
          <tbody>
            {accounts.map((account) => (
              <tr key={account.id} className="border-t border-border">
                <td className="truncate py-2 pr-2 font-medium text-foreground">{account.customer}</td>
                <td className="py-2 text-right tabular-nums text-foreground">{formatArs(account.balance)}</td>
                <td className="py-2 text-right text-xs text-muted-foreground">
                  {account.daysOverdue === 0
                    ? t('accounts.onTime')
                    : t('accounts.daysOverdue', { count: account.daysOverdue })}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </DashboardCard>
  )
}
