import { treasuryAccountTypesApi } from '@/api/treasuryAccountTypes'
import { MasterDataScreen } from './MasterDataScreen'

/**
 * Treasury account types ABM (`/treasury/account-types`): the kinds of money the business groups its accounts by
 * ("Efectivo", "Bancos", "Tarjetas de crédito"...). Treasury shows the company total split by these types.
 */
export function TreasuryAccountTypesScreen() {
  return (
    <MasterDataScreen
      namespace="treasuryAccountTypes"
      api={treasuryAccountTypesApi}
      viewKey="treasuryAccountTypes"
      nameInUseCode="treasury-account-type-name-in-use"
      keyInUseCode="treasury-account-type-key-in-use"
    />
  )
}
