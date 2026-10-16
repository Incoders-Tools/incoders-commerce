import { useEffect, useState } from 'react'
import { listPresentations, listProducts } from '@/api/catalog'
import type { StockQuantityBehavior } from '@/api/types'
import { behaviorFromCatalog } from '@/lib/quantity'

/** A presentation as the pickers show it: product and presentation joined. */
export interface PresentationOption {
  id: string
  productName: string
  presentationName: string
  behavior: StockQuantityBehavior
  identificationCode: string | null
}

export const presentationLabel = (option: Pick<PresentationOption, 'productName' | 'presentationName'>) =>
  `${option.productName} — ${option.presentationName}`

/**
 * The branch catalog (products + presentations), loaded once per screen and
 * searched client side by the pickers: a branch catalog is hundreds of rows,
 * not the ~4000 cities `CityPicker` has to search on the server.
 */
export function usePresentationOptions(enabled = true): { options: PresentationOption[]; failed: boolean; loading: boolean } {
  const [options, setOptions] = useState<PresentationOption[]>([])
  const [failed, setFailed] = useState(false)
  const [loading, setLoading] = useState(enabled)

  useEffect(() => {
    if (!enabled) return
    let current = true
    Promise.all([listProducts(), listPresentations()]).then(
      ([products, presentations]) => {
        if (!current) return
        const names = new Map(products.map((product) => [product.id, product.name]))
        setOptions(
          presentations
            .map((presentation) => ({
              id: presentation.id,
              productName: names.get(presentation.productId) ?? '',
              presentationName: presentation.name,
              behavior: behaviorFromCatalog(presentation.quantityBehavior),
              identificationCode: presentation.identificationCode,
            }))
            .sort((a, b) => presentationLabel(a).localeCompare(presentationLabel(b), 'es')),
        )
        setFailed(false)
        setLoading(false)
      },
      () => {
        if (!current) return
        setFailed(true)
        setLoading(false)
      },
    )
    return () => {
      current = false
    }
  }, [enabled])

  return { options, failed, loading }
}
