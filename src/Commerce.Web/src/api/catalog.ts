import { apiFetchOutcome } from './client'
import type { ManagementOutcome, RenameProductRequest } from './types'

export function renameProduct(
  productId: string,
  request: RenameProductRequest,
): Promise<ManagementOutcome> {
  return apiFetchOutcome<ManagementOutcome>(`/catalog/products/${productId}/rename`, {
    method: 'POST',
    body: JSON.stringify(request),
  })
}
