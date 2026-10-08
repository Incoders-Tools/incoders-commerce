import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { getOwnOrganizationSettings } from '@/api/account'
import type { StockQuantityBehavior } from '@/api/types'
import { useOptionalAuth } from '@/auth/AuthContext'
import { useOptionalOrganizationContext } from '@/organization/OrganizationContext'
import {
  formatQuantity,
  formatSignedQuantity,
  formatStockQuantity,
  parseQuantity,
  readQuantity,
  type DecimalSeparator,
  type QuantityReading,
} from '@/lib/quantity'

interface NumberFormatContextValue {
  separator: DecimalSeparator
  /** Re-reads the organization settings (after the settings screen saved a new format). */
  reload: () => void
}

const DEFAULT_VALUE: NumberFormatContextValue = { separator: 'Comma', reload: () => {} }

const NumberFormatContext = createContext<NumberFormatContextValue | undefined>(undefined)

/**
 * Loads the signed-in user's organization number format once per identity (and per organization a system
 * administrator is acting on). While loading, when the call fails or when nobody is signed in the format is the
 * default, `Comma`: quantities never block on this setting.
 */
export function NumberFormatProvider({ children }: { children: ReactNode }) {
  const auth = useOptionalAuth()
  const userId = auth?.user?.userId
  const selectedOrganizationId = useOptionalOrganizationContext()?.selectedOrganization?.id ?? null

  const [separator, setSeparator] = useState<DecimalSeparator>('Comma')
  const [reloadToken, setReloadToken] = useState(0)
  const reload = useCallback(() => setReloadToken((token) => token + 1), [])

  useEffect(() => {
    if (!userId) return
    let cancelled = false
    getOwnOrganizationSettings()
      .then((settings) => {
        if (!cancelled) setSeparator(settings.quantityDecimalSeparator === 'Dot' ? 'Dot' : 'Comma')
      })
      .catch(() => {
        if (!cancelled) setSeparator('Comma')
      })
    return () => {
      cancelled = true
    }
  }, [userId, selectedOrganizationId, reloadToken])

  // Nobody signed in (or signed out since): the default, whatever the last user's organization used.
  const effective: DecimalSeparator = userId ? separator : 'Comma'
  const value = useMemo(() => ({ separator: effective, reload }), [effective, reload])
  return <NumberFormatContext.Provider value={value}>{children}</NumberFormatContext.Provider>
}

/**
 * The organization's number format for quantities (kilos, units): parsing what the operator types and showing
 * quantities back. Money is NOT part of it. Non-throwing: without a provider it behaves as `Comma`.
 */
export function useNumberFormat() {
  const { separator, reload } = useContext(NumberFormatContext) ?? DEFAULT_VALUE
  const { t } = useTranslation('common')
  return useMemo(
    () => ({
      separator,
      reload,
      /** The number typed, or `null` when blank or not valid in this format. */
      parse: (text: string): number | null => parseQuantity(text, separator),
      read: (text: string): QuantityReading => readQuantity(text, separator),
      /** `1,5 kg` / `1.5 kg`. */
      formatStock: (quantity: number, behavior: StockQuantityBehavior) => formatStockQuantity(quantity, behavior, separator),
      /** The number alone (`117,5` / `117.5`). */
      formatNumber: (quantity: number) => formatQuantity(quantity, separator),
      /** `+120`, `-2,5` / `-2.5`. */
      formatSigned: (quantity: number) => formatSignedQuantity(quantity, separator),
      /** Placeholder / hint: `Ej: 1,5` or `Ej: 1.5`. */
      example: t(`numberFormat.example.${separator}`),
      /** The format message when the text is a number written with the other convention, else `null`. */
      errorFor: (text: string): string | null => {
        const reading = readQuantity(text, separator)
        return !reading.ok && reading.reason === 'separator' ? t(`numberFormat.errors.${separator}`) : null
      },
    }),
    [separator, reload, t],
  )
}
