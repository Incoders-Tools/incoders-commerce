import { apiFetch, apiFetchOutcome } from './client'
import type {
  CreatePresentationRequest,
  CreateProductRequest,
  ManagementOutcome,
  PresentationRecord,
  ProductRecord,
  RenameProductRequest,
  UpdatePresentationRequest,
} from './types'

export function renameProduct(
  productId: string,
  request: RenameProductRequest,
): Promise<ManagementOutcome> {
  return apiFetchOutcome<ManagementOutcome>(`/catalog/products/${productId}/rename`, {
    method: 'POST',
    body: JSON.stringify(request),
  })
}

export function listProducts(): Promise<ProductRecord[]> {
  return apiFetch<ProductRecord[]>('/catalog/products')
}

export function createProduct(request: CreateProductRequest): Promise<ProductRecord> {
  return apiFetch<ProductRecord>('/catalog/products', {
    method: 'POST',
    body: JSON.stringify(request),
  })
}

export function listPresentations(): Promise<PresentationRecord[]> {
  return apiFetch<PresentationRecord[]>('/catalog/presentations')
}

export function createPresentation(request: CreatePresentationRequest): Promise<PresentationRecord> {
  return apiFetch<PresentationRecord>('/catalog/presentations', {
    method: 'POST',
    body: JSON.stringify(request),
  })
}

export function updatePresentation(
  presentationId: string,
  request: UpdatePresentationRequest,
): Promise<PresentationRecord> {
  return apiFetch<PresentationRecord>(`/catalog/presentations/${presentationId}`, {
    method: 'PUT',
    body: JSON.stringify(request),
  })
}
