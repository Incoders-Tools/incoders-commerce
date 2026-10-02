import { apiFetch } from './client'
import type {
  StockAdjustmentRequest,
  StockAdjustmentResult,
  StockHistoryPage,
  StockLevel,
  StockMinimum,
} from './types'

export function listStock(filters: { search?: string; onlyBelowMinimum?: boolean } = {}): Promise<StockLevel[]> {
  const query = new URLSearchParams()
  if (filters.search) query.set('search', filters.search)
  if (filters.onlyBelowMinimum) query.set('onlyBelowMinimum', 'true')
  const queryString = query.toString()
  return apiFetch<StockLevel[]>(queryString ? `/stock?${queryString}` : '/stock')
}

export function listStockMovements(presentationId: string, page = 1, pageSize = 25): Promise<StockHistoryPage> {
  return apiFetch<StockHistoryPage>(`/stock/${presentationId}/movements?page=${page}&pageSize=${pageSize}`)
}

export function adjustStock(request: StockAdjustmentRequest): Promise<StockAdjustmentResult> {
  return apiFetch<StockAdjustmentResult>('/stock/adjustments', { method: 'POST', body: JSON.stringify(request) })
}

export function setStockMinimum(presentationId: string, minimumQuantity: number | null): Promise<StockMinimum> {
  return apiFetch<StockMinimum>(`/stock/minimums/${presentationId}`, {
    method: 'PUT',
    body: JSON.stringify({ minimumQuantity }),
  })
}
