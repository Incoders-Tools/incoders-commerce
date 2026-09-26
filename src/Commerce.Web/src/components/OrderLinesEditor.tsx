import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { Label } from '@/components/ui/label'
import type { PresentationRecord, SubmitOrderLine } from '@/api/types'

/**
 * commerce-guest-ordering design.md "One screen, guest and registered as
 * peers": a shared presentation-picker line editor fed by the public
 * catalog read, reused by BOTH the guest and registered branches of
 * `OrderScreen`, so the money-relevant line-item UI is written once. Lines
 * are addressed by presentation NAME here — `productId`/`presentationId`
 * are resolved internally from the selected `PresentationRecord`, never
 * typed by the caller (the raw-GUID shape this component replaces).
 */
export function OrderLinesEditor({
  presentations,
  lines,
  onChange,
}: {
  presentations: PresentationRecord[]
  lines: SubmitOrderLine[]
  onChange: (lines: SubmitOrderLine[]) => void
}) {
  const { t } = useTranslation('orders')
  const [selectedPresentationId, setSelectedPresentationId] = useState(presentations[0]?.id ?? '')
  const [quantity, setQuantity] = useState('1')

  const presentationById = (id: string) => presentations.find((p) => p.id === id)

  const handleAddLine = () => {
    const presentation = presentationById(selectedPresentationId)
    const parsedQuantity = Number(quantity)
    if (!presentation || !Number.isFinite(parsedQuantity) || parsedQuantity <= 0) {
      return
    }
    onChange([
      ...lines,
      { productId: presentation.productId, presentationId: presentation.id, quantity: parsedQuantity },
    ])
  }

  const handleRemoveLine = (index: number) => {
    onChange(lines.filter((_, i) => i !== index))
  }

  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-col gap-1.5">
        <Label htmlFor="order-line-presentation">{t('lineEditor.presentationLabel')}</Label>
        <Select
          id="order-line-presentation"
          value={selectedPresentationId}
          onChange={(e) => setSelectedPresentationId(e.target.value)}
        >
          {presentations.map((presentation) => (
            <option key={presentation.id} value={presentation.id}>
              {presentation.name}
            </option>
          ))}
        </Select>
      </div>
      <div className="flex flex-col gap-1.5">
        <Label htmlFor="order-line-quantity">{t('lineEditor.quantityLabel')}</Label>
        <Input
          id="order-line-quantity"
          type="number"
          min={1}
          value={quantity}
          onChange={(e) => setQuantity(e.target.value)}
        />
      </div>
      <Button type="button" variant="outline" onClick={handleAddLine} disabled={presentations.length === 0}>
        {t('lineEditor.addLine')}
      </Button>

      {lines.length > 0 && (
        <ul className="flex flex-col gap-1.5 text-sm">
          {lines.map((line, index) => {
            const presentation = presentationById(line.presentationId)
            return (
              <li key={`${line.presentationId}-${index}`} className="flex items-center justify-between gap-2">
                <span>
                  {t('lineEditor.lineSummary', { name: presentation?.name ?? line.presentationId, quantity: line.quantity })}
                </span>
                <Button type="button" variant="outline" size="sm" onClick={() => handleRemoveLine(index)}>
                  {t('lineEditor.remove')}
                </Button>
              </li>
            )
          })}
        </ul>
      )}
    </div>
  )
}
