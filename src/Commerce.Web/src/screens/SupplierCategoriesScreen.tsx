import { supplierCategoriesApi } from '@/api/supplierCategories'
import { MasterDataScreen } from './MasterDataScreen'

/** Supplier categories ("rubros") ABM (`/suppliers/categories`); suppliers pick one of these. */
export function SupplierCategoriesScreen() {
  return (
    <MasterDataScreen
      namespace="supplierCategories"
      api={supplierCategoriesApi}
      viewKey="supplierCategories"
      nameInUseCode="supplier-category-name-in-use"
      keyInUseCode="supplier-category-key-in-use"
    />
  )
}
