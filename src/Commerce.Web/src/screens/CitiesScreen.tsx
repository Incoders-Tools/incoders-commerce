import { citiesApi } from '@/api/cities'
import { MasterDataScreen } from './MasterDataScreen'

/** Cities catalog ABM (`/customers/cities`); customers pick one of these. */
export function CitiesScreen() {
  return (
    <MasterDataScreen
      namespace="cities"
      api={citiesApi}
      viewKey="cities"
      nameInUseCode="city-name-in-use"
      keyInUseCode="city-key-in-use"
    />
  )
}
