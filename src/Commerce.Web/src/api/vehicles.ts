import { apiFetch } from './client'

// Endpoints/Vehicles.cs + Persistence/PostgresVehicleStore.cs, mirrored. The business's vehicles (vehicles.md): a lookup
// table of the organization, listed by plate; a delivery run picks one.

export interface Vehicle {
  id: string
  /** Kept in capitals without spaces or dashes ("AB123CD"). */
  plate: string
  brand: string | null
  model: string | null
  year: number | null
  notes: string | null
  isActive: boolean
  /** How it reads on a delivery run: "AB123CD · Ford F-4000". */
  label: string
  createdAtUtc: string
  updatedAtUtc: string
}

export interface VehicleRequest {
  /** Written any way: the server normalizes it. */
  plate: string
  brand: string | null
  model: string | null
  year: number | null
  notes: string | null
  /** Omitted: active when created, kept when edited. */
  isActive?: boolean
}

export function listVehicles(includeInactive = false): Promise<Vehicle[]> {
  return apiFetch<Vehicle[]>(includeInactive ? '/vehicles?includeInactive=true' : '/vehicles')
}

export function createVehicle(request: VehicleRequest): Promise<Vehicle> {
  return apiFetch<Vehicle>('/vehicles', { method: 'POST', body: JSON.stringify(request) })
}

export function updateVehicle(id: string, request: VehicleRequest): Promise<Vehicle> {
  return apiFetch<Vehicle>(`/vehicles/${id}`, { method: 'PUT', body: JSON.stringify(request) })
}
