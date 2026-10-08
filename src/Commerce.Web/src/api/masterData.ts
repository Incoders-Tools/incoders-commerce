import { apiFetch } from './client'
import type { MasterDataEntry, MasterDataRequest } from './types'

export interface MasterDataApi {
  list(includeInactive?: boolean): Promise<MasterDataEntry[]>
  create(request: MasterDataRequest): Promise<MasterDataEntry>
  update(id: string, request: MasterDataRequest): Promise<MasterDataEntry>
}

/** Client for one organization-scoped customer catalog (cities, business types). */
export function createMasterDataApi(basePath: string): MasterDataApi {
  return {
    list: (includeInactive = false) =>
      apiFetch<MasterDataEntry[]>(includeInactive ? `${basePath}?includeInactive=true` : basePath),
    create: (request) => apiFetch<MasterDataEntry>(basePath, { method: 'POST', body: JSON.stringify(request) }),
    update: (id, request) =>
      apiFetch<MasterDataEntry>(`${basePath}/${id}`, { method: 'PUT', body: JSON.stringify(request) }),
  }
}
