import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Label } from '@/components/ui/label'
import { Select } from '@/components/ui/select'
import { Textarea } from '@/components/ui/textarea'
import { FormPage } from '@/components/layout/FormPage'
import { Field, FormSection } from '@/components/form/FormParts'
import { PresentationPicker } from '@/components/purchasing/PresentationPicker'
import { usePresentationOptions, type PresentationOption } from '@/components/purchasing/presentationOptions'
import { adjustStock } from '@/api/stock'
import { ApiError } from '@/api/client'
import type { ManualStockKind } from '@/api/types'
import { unitLabel } from '@/lib/quantity'
import { useNumberFormat } from '@/organization/NumberFormatContext'

const KINDS: ManualStockKind[] = ['Opening', 'Shrinkage', 'CountCorrection', 'Adjustment']
type Direction = 'add' | 'subtract'

/** Opening always adds and shrinkage always subtracts; the other two kinds are the operator's call. */
const fixedDirection = (kind: ManualStockKind): Direction | null =>
  kind === 'Opening' ? 'add' : kind === 'Shrinkage' ? 'subtract' : null

interface StockAdjustFormProps {
  /** Preselected presentation (adjusting from a row); null to choose one. */
  presentation: PresentationOption | null
  onCancel: () => void
  onDone: (message: string) => void
}

/**
 * Manual stock adjustment (`POST /stock/adjustments`). The operator types the
 * quantity without a sign and the form says in plain words what the kind does
 * to the stock; kinds that can go either way make them pick "Suma" or "Resta"
 * with no default, so a sign is never guessed.
 */
export function StockAdjustForm({ presentation: initial, onCancel, onDone }: StockAdjustFormProps) {
  const { t } = useTranslation('stock')
  const numberFormat = useNumberFormat()
  const { options, failed } = usePresentationOptions()
  const [presentation, setPresentation] = useState<PresentationOption | null>(initial)
  const [kind, setKind] = useState<ManualStockKind | ''>('')
  const [direction, setDirection] = useState<Direction | ''>('')
  const [quantity, setQuantity] = useState('')
  const [reason, setReason] = useState('')
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  const forced = kind === '' ? null : fixedDirection(kind)
  const unit = presentation ? unitLabel(presentation.behavior) : null

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    setError(null)
    const found: Record<string, string> = {}
    const magnitude = numberFormat.parse(quantity)
    if (!presentation) found.presentation = t('adjustForm.errors.presentation')
    if (kind === '') found.kind = t('adjustForm.errors.kind')
    else if (forced === null && direction === '') found.direction = t('adjustForm.errors.direction')
    if (magnitude === null || magnitude <= 0) found.quantity = numberFormat.errorFor(quantity) ?? t('adjustForm.errors.quantity')
    else if (presentation?.behavior === 'FixedQuantity' && !Number.isInteger(magnitude)) {
      found.quantity = t('adjustForm.errors.quantityInteger')
    } else if (Math.round(magnitude * 1000) / 1000 !== magnitude) found.quantity = t('adjustForm.errors.quantityDecimals')
    if (reason.trim() === '') found.reason = t('adjustForm.errors.reason')
    setErrors(found)
    if (Object.keys(found).length > 0 || !presentation || kind === '' || magnitude === null) return

    const effective = forced ?? (direction as Direction)
    setSubmitting(true)
    try {
      const result = await adjustStock({
        presentationId: presentation.id,
        kind,
        quantity: effective === 'add' ? magnitude : -magnitude,
        reason: reason.trim(),
      })
      onDone(t('adjustForm.done', { onHand: numberFormat.formatStock(result.onHand, presentation.behavior) }))
    } catch (err) {
      if (err instanceof ApiError && err.status === 400 && err.fieldErrors) {
        const server: Record<string, string> = {}
        for (const [key, messages] of Object.entries(err.fieldErrors)) server[key] = messages[0]
        setErrors(server)
        if (!server.quantity && !server.reason && !server.presentationId) setError(err.message)
      } else {
        setError(err instanceof ApiError ? err.message : t('errors.unexpectedSave'))
      }
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <FormPage title={t('adjustForm.title')} onBack={onCancel} backLabel={t('adjustForm.backLabel')}>
      <form className="flex flex-col gap-8" onSubmit={(event) => void submit(event)}>
        <FormSection title={t('adjustForm.title')}>
          <PresentationPicker
            label={t('adjustForm.presentation')}
            options={options}
            loadFailed={failed}
            value={presentation}
            error={errors.presentation ?? errors.presentationId ?? null}
            onChange={(option) => {
              setPresentation(option)
              setErrors((current) => ({ ...current, presentation: '' }))
            }}
          />
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="adjustKind">{t('adjustForm.kind')}</Label>
            <Select
              id="adjustKind"
              value={kind}
              aria-invalid={errors.kind ? true : undefined}
              onChange={(event) => {
                setKind(event.target.value as ManualStockKind | '')
                setDirection('')
                setErrors((current) => ({ ...current, kind: '', direction: '' }))
              }}
            >
              <option value="">{t('adjustForm.kindPlaceholder')}</option>
              {KINDS.map((value) => (
                <option key={value} value={value}>
                  {t(`adjustForm.kinds.${value}`)}
                </option>
              ))}
            </Select>
            {errors.kind ? (
              <p className="text-xs text-destructive">{errors.kind}</p>
            ) : (
              kind !== '' && <p className="text-xs text-muted-foreground">{t(`adjustForm.kindHelp.${kind}`)}</p>
            )}
          </div>
          {kind !== '' && forced === null && (
            <fieldset className="flex flex-col gap-1.5">
              <legend className="text-sm font-medium text-foreground">{t('adjustForm.direction')}</legend>
              <div className="flex gap-4">
                {(['add', 'subtract'] as const).map((value) => (
                  <label key={value} className="flex items-center gap-2 text-sm">
                    <input
                      type="radio"
                      name="direction"
                      checked={direction === value}
                      onChange={() => {
                        setDirection(value)
                        setErrors((current) => ({ ...current, direction: '' }))
                      }}
                    />
                    {t(value === 'add' ? 'adjustForm.directionAdd' : 'adjustForm.directionSubtract')}
                  </label>
                ))}
              </div>
              {errors.direction && <p className="text-xs text-destructive">{errors.direction}</p>}
            </fieldset>
          )}
          <Field
            id="adjustQuantity"
            label={t('adjustForm.quantity')}
            value={quantity}
            onChange={(value) => {
              setQuantity(value)
              setErrors((current) => ({ ...current, quantity: '' }))
            }}
            hint={unit ? t('adjustForm.quantityHint', { unit }) : undefined}
            placeholder={numberFormat.example}
            inputMode="decimal"
            error={errors.quantity || null}
          />
        </FormSection>

        <FormSection title={t('adjustForm.reason')} wide>
          <Label htmlFor="adjustReason" className="sr-only">
            {t('adjustForm.reason')}
          </Label>
          <Textarea
            id="adjustReason"
            rows={3}
            value={reason}
            aria-label={t('adjustForm.reason')}
            aria-invalid={errors.reason ? true : undefined}
            onChange={(event) => {
              setReason(event.target.value)
              setErrors((current) => ({ ...current, reason: '' }))
            }}
          />
          {errors.reason && <p className="text-xs text-destructive">{errors.reason}</p>}
        </FormSection>

        {error && (
          <p role="alert" className="text-sm text-destructive">
            {error}
          </p>
        )}

        <div className="flex gap-2">
          <Button type="submit" disabled={submitting}>
            {submitting ? t('adjustForm.submitting') : t('adjustForm.submit')}
          </Button>
          <Button type="button" variant="outline" onClick={onCancel} disabled={submitting}>
            {t('minimum.cancel')}
          </Button>
        </div>
      </form>
    </FormPage>
  )
}
