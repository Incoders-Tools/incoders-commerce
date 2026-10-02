import { apiFetch } from './client'
import type { GeoCity, GeoCityFilters, GeoCityRequest, GeoProvince } from './types'

export function listProvinces(): Promise<GeoProvince[]> {
  return apiFetch<GeoProvince[]>('/geo/provinces')
}

/** Server-side search (accent/case-insensitive, prefix first), paged by `limit`/`offset`. */
export function listCities(filters: GeoCityFilters = {}): Promise<GeoCity[]> {
  const query = new URLSearchParams()
  if (filters.search) query.set('search', filters.search)
  if (filters.provinceId) query.set('provinceId', filters.provinceId)
  if (filters.limit !== undefined) query.set('limit', String(filters.limit))
  if (filters.offset) query.set('offset', String(filters.offset))
  if (filters.includeInactive) query.set('includeInactive', 'true')
  const queryString = query.toString()
  return apiFetch<GeoCity[]>(queryString ? `/geo/cities?${queryString}` : '/geo/cities')
}

export function createCity(request: GeoCityRequest): Promise<GeoCity> {
  return apiFetch<GeoCity>('/geo/cities', { method: 'POST', body: JSON.stringify(request) })
}

export function updateCity(id: string, request: GeoCityRequest): Promise<GeoCity> {
  return apiFetch<GeoCity>(`/geo/cities/${id}`, { method: 'PUT', body: JSON.stringify(request) })
}
