import { createMasterDataApi } from './masterData'

/** The organization's kinds of money ("Efectivo", "Bancos", "Tarjetas de crédito"...): every treasury account has one. */
export const treasuryAccountTypesApi = createMasterDataApi('/treasury/account-types')
