import { apiFetch } from './client'
import type { CategoryRecord, CategoryRequest } from './types'

// catalog-categories: organization-scoped product categories. Reading is open
// to any member; writing needs the catalog-management permission (server side).
export function listCategories(): Promise<CategoryRecord[]> {
  return apiFetch<CategoryRecord[]>('/catalog/categories')
}

export function createCategory(request: CategoryRequest): Promise<CategoryRecord> {
  return apiFetch<CategoryRecord>('/catalog/categories', {
    method: 'POST',
    body: JSON.stringify(request),
  })
}

export function updateCategory(categoryId: string, request: CategoryRequest): Promise<CategoryRecord> {
  return apiFetch<CategoryRecord>(`/catalog/categories/${categoryId}`, {
    method: 'PUT',
    body: JSON.stringify(request),
  })
}

export function deleteCategory(categoryId: string): Promise<void> {
  return apiFetch<void>(`/catalog/categories/${categoryId}`, { method: 'DELETE' })
}
