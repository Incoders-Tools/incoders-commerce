import { useEffect, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select } from '@/components/ui/select'
import { FormPage } from '@/components/layout/FormPage'
import { tomorrowIso } from '@/lib/isoDate'
import { ApiError } from '@/api/client'
import { floorViolationsOf, getComposition, publishComposition } from '@/api/pricing'
import type { CalculationBase, FloorViolation, PriceListRecord, RateComponent } from '@/api/types'
import { FloorViolationsTable } from './FloorViolationsTable'

interface CompositionFormProps {
  priceList: PriceListRecord
  onBack: () => void
  onPublished: () => void
}

type Mode = 'components' | 'markup'

interface Draft {
  key: number
  code: string
  label: string
  percentage: string
  calculationBase: CalculationBase
  order: string
}

const draftOf = (key: number, component?: RateComponent): Draft => ({
  key,
  code: component?.code ?? '',
  label: component?.label ?? '',
  percentage: component ? String(component.percentage) : '',
  calculationBase: component?.calculationBase ?? 'Base',
  order: component ? String(component.order) : '',
})

/**
 * Publishes a new effective-dated composition for a list. Never edits the version in force: the
 * server keeps every version, so a mistake is corrected by publishing another one. Two ways to
 * write it: the full component set, or "solo remarcación" for the common markup change.
 */
export function CompositionForm({ priceList, onBack, onPublished }: CompositionFormProps) {
  const { t } = useTranslation('priceLists')
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [drafts, setDrafts] = useState<Draft[]>([])
  const [nextKey, setNextKey] = useState(1)
  const [mode, setMode] = useState<Mode>('components')
  const [markup, setMarkup] = useState('')
  const [effectiveFrom, setEffectiveFrom] = useState(tomorrowIso)
  const [error, setError] = useState<string | null>(null)
  const [violations, setViolations] = useState<FloorViolation[] | null>(null)
  const [submitting, setSubmitting] = useState(false)

  useEffect(() => {
    let cancelled = false
    getComposition(priceList.id)
      .then((current) => {
        if (cancelled) return
        const ordered = [...current.components].sort((a, b) => a.order - b.order)
        setDrafts(ordered.map((component, index) => draftOf(index + 1, component)))
        setNextKey(ordered.length + 1)
      })
      .catch((err) => {
        if (!cancelled) setLoadError(err instanceof ApiError ? err.message : t('compositionForm.errors.unexpectedLoad'))
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })
    return () => {
      cancelled = true
    }
  }, [priceList.id, t])

  const update = (key: number, patch: Partial<Draft>) =>
    setDrafts((current) => current.map((draft) => (draft.key === key ? { ...draft, ...patch } : draft)))

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault()
    setError(null)
    setViolations(null)

    let request
    if (mode === 'markup') {
      if (markup.trim() === '') return setError(t('compositionForm.errors.markupRequired'))
      request = { effectiveFrom, remarcacionPercentage: Number(markup) }
    } else {
      const incomplete = drafts.some(
        (draft) => draft.label.trim() === '' || draft.code.trim() === '' || draft.percentage.trim() === '',
      )
      if (incomplete) return setError(t('compositionForm.errors.incomplete'))
      request = {
        effectiveFrom,
        components: drafts.map((draft, index) => ({
          code: draft.code.trim(),
          label: draft.label.trim(),
          percentage: Number(draft.percentage),
          calculationBase: draft.calculationBase,
          order: draft.order.trim() === '' ? index + 1 : Number(draft.order),
        })),
      }
    }

    setSubmitting(true)
    try {
      await publishComposition(priceList.id, request)
      onPublished()
    } catch (err) {
      const refused = floorViolationsOf(err)
      if (refused) setViolations(refused)
      else if (err instanceof ApiError && err.status === 409 && err.code === 'composition-already-exists-for-date') {
        setError(t('compositionForm.errors.dateTaken'))
      } else {
        setError(err instanceof ApiError ? err.message : t('compositionForm.errors.unexpected'))
      }
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <FormPage
      title={t('compositionForm.title', { name: priceList.name })}
      description={t('compositionForm.description')}
      onBack={onBack}
      backLabel={t('breakdown.backLabel')}
    >
      {loading ? (
        <p role="status" className="text-sm text-muted-foreground">{t('compositionForm.loading')}</p>
      ) : (
        <form className="flex flex-col gap-6" onSubmit={(event) => void handleSubmit(event)}>
          {loadError && (
            <p role="alert" className="text-sm text-destructive">{loadError}</p>
          )}

          <div className="flex max-w-xs flex-col gap-1.5">
            <Label htmlFor="effectiveFrom">{t('compositionForm.effectiveFrom')}</Label>
            <Input
              id="effectiveFrom"
              type="date"
              min={tomorrowIso()}
              value={effectiveFrom}
              onChange={(e) => setEffectiveFrom(e.target.value)}
              required
            />
            <p className="text-xs text-muted-foreground">{t('compositionForm.effectiveFromHint')}</p>
          </div>

          <fieldset className="flex flex-col gap-2">
            <legend className="mb-1 text-sm font-semibold">{t('compositionForm.mode')}</legend>
            <div className="flex flex-wrap gap-4 text-sm">
              {(['components', 'markup'] as const).map((option) => (
                <label key={option} className="flex items-center gap-2">
                  <input
                    type="radio"
                    name="composition-mode"
                    checked={mode === option}
                    onChange={() => setMode(option)}
                  />
                  {t(option === 'components' ? 'compositionForm.modeComponents' : 'compositionForm.modeMarkup')}
                </label>
              ))}
            </div>
          </fieldset>

          {mode === 'markup' ? (
            <div className="flex max-w-xs flex-col gap-1.5">
              <Label htmlFor="markup">{t('compositionForm.markup')}</Label>
              <Input
                id="markup"
                type="number"
                step="0.01"
                value={markup}
                onChange={(e) => setMarkup(e.target.value)}
              />
              <p className="text-xs text-muted-foreground">{t('compositionForm.markupHint')}</p>
            </div>
          ) : (
            <fieldset className="flex flex-col gap-3">
              <legend className="mb-1 text-sm font-semibold">{t('compositionForm.componentsTitle')}</legend>
              {drafts.map((draft, position) => {
                const index = position + 1
                return (
                  <div key={draft.key} className="grid grid-cols-2 items-end gap-3 rounded-md border border-border p-3 md:grid-cols-[2fr_1fr_1fr_1.5fr_1fr_auto]">
                    <Input
                      aria-label={t('compositionForm.label', { index })}
                      value={draft.label}
                      onChange={(e) => update(draft.key, { label: e.target.value })}
                    />
                    <Input
                      aria-label={t('compositionForm.code', { index })}
                      value={draft.code}
                      onChange={(e) => update(draft.key, { code: e.target.value })}
                    />
                    <Input
                      aria-label={t('compositionForm.percentage', { index })}
                      type="number"
                      step="0.01"
                      value={draft.percentage}
                      onChange={(e) => update(draft.key, { percentage: e.target.value })}
                    />
                    <Select
                      aria-label={t('compositionForm.base', { index })}
                      value={draft.calculationBase}
                      onChange={(e) => update(draft.key, { calculationBase: e.target.value as CalculationBase })}
                    >
                      <option value="Base">{t('compositionForm.baseOptions.Base')}</option>
                      <option value="Subtotal">{t('compositionForm.baseOptions.Subtotal')}</option>
                    </Select>
                    <Input
                      aria-label={t('compositionForm.order', { index })}
                      type="number"
                      value={draft.order}
                      placeholder={String(index)}
                      onChange={(e) => update(draft.key, { order: e.target.value })}
                    />
                    <Button
                      type="button"
                      variant="outline"
                      size="sm"
                      aria-label={t('compositionForm.remove', { index })}
                      onClick={() => setDrafts((current) => current.filter((candidate) => candidate.key !== draft.key))}
                    >
                      ×
                    </Button>
                  </div>
                )
              })}
              <div>
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  onClick={() => {
                    setDrafts((current) => [...current, draftOf(nextKey)])
                    setNextKey((key) => key + 1)
                  }}
                >
                  {t('compositionForm.add')}
                </Button>
              </div>
            </fieldset>
          )}

          {error && (
            <p role="alert" className="text-sm text-destructive">{error}</p>
          )}
          {violations && <FloorViolationsTable violations={violations} />}

          <div className="flex gap-2">
            <Button type="submit" disabled={submitting}>
              {submitting ? t('compositionForm.publishing') : t('compositionForm.publish')}
            </Button>
            <Button type="button" variant="outline" onClick={onBack} disabled={submitting}>
              {t('compositionForm.cancel')}
            </Button>
          </div>
        </form>
      )}
    </FormPage>
  )
}
