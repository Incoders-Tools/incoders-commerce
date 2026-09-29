import { apiFetch, apiFetchOutcome } from './client'
import type {
  CopyCatalogRequest,
  CopyCatalogResponse,
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

// catalog-categories: `PUT /catalog/products/{id}/category` — the server
// refuses a category that does not belong to the caller's organization.
export function changeProductCategory(productId: string, categoryId: string): Promise<ProductRecord> {
  return apiFetch<ProductRecord>(`/catalog/products/${productId}/category`, {
    method: 'PUT',
    body: JSON.stringify({ categoryId }),
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

// B7 U5b: `POST /catalog/copy` — the header-selected branch is the SOURCE.
export function copyCatalog(request: CopyCatalogRequest): Promise<CopyCatalogResponse> {
  return apiFetch<CopyCatalogResponse>('/catalog/copy', {
    method: 'POST',
    body: JSON.stringify(request),
  })
}
