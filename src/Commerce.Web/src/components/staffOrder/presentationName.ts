import type { StaffPresentationOption } from '@/api/types'

/** How a line or search result names a presentation: `Chorizo Paquete`. */
export const presentationName = (option: Pick<StaffPresentationOption, 'productName' | 'presentationName'>) =>
  `${option.productName} ${option.presentationName}`
