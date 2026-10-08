import { businessTypesApi } from '@/api/businessTypes'
import { MasterDataScreen } from './MasterDataScreen'

/** Business types catalog ABM (`/customers/business-types`); customers pick one of these. */
export function BusinessTypesScreen() {
  return (
    <MasterDataScreen
      namespace="businessTypes"
      api={businessTypesApi}
      viewKey="businessTypes"
      nameInUseCode="business-type-name-in-use"
      keyInUseCode="business-type-key-in-use"
    />
  )
}
