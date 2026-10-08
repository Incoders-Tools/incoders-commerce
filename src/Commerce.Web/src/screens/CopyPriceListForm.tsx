import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select } from '@/components/ui/select'
import { FormPage } from '@/components/layout/FormPage'
import { todayIso } from '@/lib/isoDate'
import { ApiError } from '@/api/client'
import { copyPriceList, floorViolationsOf } from '@/api/pricing'
import type { CopyPriceListRequest, CopyPriceListResponse, FloorViolation, PriceListRecord } from '@/api/types'
import { FloorViolationsTable } from './FloorViolationsTable'

/** `<select>` values for the two non-list floor choices; list choices use the list id. */
const KEEP_FLOOR = ''
const NO_FLOOR = 'none'

interface CopyPriceListFormProps {
  source: PriceListRecord
  priceLists: PriceListRecord[]
  onBack: () => void
  onCopied: (result: CopyPriceListResponse) => void
}

/**
 * "Copiar lista": a new, independent list with the source's base prices and its composition with a
 * different markup. The copy is never the default list; it carries the source's floor unless told
 * otherwise. The source list is untouched.
 */
export function CopyPriceListForm({ source, priceLists, onBack, onCopied }: CopyPriceListFormProps) {
  const { t } = useTranslation('priceLists')
  const [name, setName] = useState('')
  const [markup, setMarkup] = useState('')
  const [effectiveFrom, setEffectiveFrom] = useState(todayIso)
  const [floor, setFloor] = useState(KEEP_FLOOR)
  const [error, setError] = useState<string | null>(null)
  const [violations, setViolations] = useState<FloorViolation[] | null>(null)
  const [submitting, setSubmitting] = useState(false)

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault()
    setError(null)
    setViolations(null)
    if (name.trim() === '' || markup.trim() === '') {
      setError(t('copyForm.errors.invalid'))
      return
    }
    const request: CopyPriceListRequest = {
      name: name.trim(),
      effectiveFrom,
      remarcacionPercentage: Number(markup),
      ...(floor === NO_FLOOR ? { clearFloor: true } : floor !== KEEP_FLOOR ? { floorPriceListId: floor } : {}),
    }
    setSubmitting(true)
    try {
      onCopied(await copyPriceList(source.id, request))
    } catch (err) {
      const refused = floorViolationsOf(err)
      if (refused) setViolations(refused)
      else if (err instanceof ApiError && err.status === 409 && err.code === 'price-list-name-taken') {
        setError(t('copyForm.errors.nameTaken'))
      } else if (err instanceof ApiError && err.status === 400) {
        setError(t('copyForm.errors.invalid'))
      } else {
        setError(err instanceof ApiError ? err.message : t('copyForm.errors.unexpected'))
      }
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <FormPage
      title={t('copyForm.title', { name: source.name })}
      description={t('copyForm.description')}
      onBack={onBack}
      backLabel={t('breakdown.backLabel')}
    >
      <form className="flex max-w-xl flex-col gap-5" onSubmit={(event) => void handleSubmit(event)}>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="copy-name">{t('copyForm.name')}</Label>
          <Input id="copy-name" value={name} onChange={(e) => setName(e.target.value)} required />
        </div>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="copy-markup">{t('copyForm.markup')}</Label>
          <Input
            id="copy-markup"
            type="number"
            step="0.01"
            value={markup}
            onChange={(e) => setMarkup(e.target.value)}
            required
          />
          <p className="text-xs text-muted-foreground">{t('copyForm.markupHint')}</p>
        </div>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="copy-effective-from">{t('copyForm.effectiveFrom')}</Label>
          <Input
            id="copy-effective-from"
            type="date"
            value={effectiveFrom}
            onChange={(e) => setEffectiveFrom(e.target.value)}
            required
          />
        </div>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="copy-floor">{t('copyForm.floor')}</Label>
          <Select id="copy-floor" value={floor} onChange={(e) => setFloor(e.target.value)}>
            <option value={KEEP_FLOOR}>{t('copyForm.floorKeep')}</option>
            <option value={NO_FLOOR}>{t('copyForm.floorNone')}</option>
            {priceLists.map((list) => (
              <option key={list.id} value={list.id}>
                {list.name}
              </option>
            ))}
          </Select>
        </div>

        {error && (
          <p role="alert" className="text-sm text-destructive">{error}</p>
        )}
        {violations && <FloorViolationsTable violations={violations} />}

        <div className="flex gap-2">
          <Button type="submit" disabled={submitting}>
            {submitting ? t('copyForm.submitting') : t('copyForm.submit')}
          </Button>
          <Button type="button" variant="outline" onClick={onBack} disabled={submitting}>
            {t('copyForm.cancel')}
          </Button>
        </div>
      </form>
    </FormPage>
  )
}
