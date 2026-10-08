import { useEffect, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { getBranchDiscountPin, setBranchDiscountPin } from '@/api/account'
import type { BranchDiscountPinStatus, BranchSummary } from '@/api/types'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'

const PIN_PATTERN = /^\d{4,12}$/

/**
 * branch-discount-pin: sets or rotates the ONE shared discount PIN of a
 * branch. The PIN is write-only: this panel shows whether one is set and when
 * it last changed, and the typed value is cleared as soon as it is sent and
 * never rendered back.
 */
export function DiscountPinPanel({ branch, onClose }: { branch: BranchSummary; onClose: () => void }) {
  const { t } = useTranslation('branches')
  const [status, setStatus] = useState<BranchDiscountPinStatus | null>(null)
  const [loadFailed, setLoadFailed] = useState(false)
  const [pin, setPin] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)

  useEffect(() => {
    let cancelled = false
    setStatus(null)
    setLoadFailed(false)
    setPin('')
    setError(null)
    getBranchDiscountPin(branch.branchId)
      .then((result) => {
        if (!cancelled) setStatus(result)
      })
      .catch(() => {
        if (!cancelled) setLoadFailed(true)
      })
    return () => {
      cancelled = true
    }
  }, [branch.branchId])

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    setError(null)
    if (!PIN_PATTERN.test(pin)) {
      setError(t('discountPin.invalid'))
      return
    }
    setSaving(true)
    try {
      setStatus(await setBranchDiscountPin(branch.branchId, pin))
      setPin('')
    } catch {
      setError(t('discountPin.saveError'))
    } finally {
      setSaving(false)
    }
  }

  const statusText = loadFailed
    ? t('discountPin.loadError')
    : status === null
      ? t('discountPin.loading')
      : status.isSet
        ? t('discountPin.set', { date: status.changedAtUtc ? new Date(status.changedAtUtc).toLocaleString() : '' })
        : t('discountPin.unset')

  return (
    <section aria-label={t('discountPin.title')} className="flex flex-col gap-3 rounded-md border bg-card p-4">
      <div className="flex items-start justify-between gap-2">
        <div>
          <h2 className="text-base font-semibold">
            {t('discountPin.title')} — {branch.branchName}
          </h2>
          <p className="text-sm text-muted-foreground">{t('discountPin.description', { name: branch.branchName })}</p>
        </div>
        <Button type="button" variant="outline" onClick={onClose}>
          {t('discountPin.close')}
        </Button>
      </div>

      <p className="text-sm">{statusText}</p>

      <form onSubmit={submit} className="flex flex-wrap items-center gap-2">
        <Input
          type="password"
          inputMode="numeric"
          autoComplete="off"
          aria-label={t('discountPin.newLabel')}
          placeholder={t('discountPin.hint')}
          className="sm:w-56"
          maxLength={12}
          value={pin}
          onChange={(e) => setPin(e.target.value)}
        />
        <Button type="submit" disabled={saving || status === null}>
          {saving ? t('discountPin.saving') : status?.isSet ? t('discountPin.change') : t('discountPin.define')}
        </Button>
      </form>

      {error && (
        <p role="alert" className="text-sm text-destructive">
          {error}
        </p>
      )}
    </section>
  )
}
