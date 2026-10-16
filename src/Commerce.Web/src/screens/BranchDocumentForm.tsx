import { useEffect, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { FormPage } from '@/components/layout/FormPage'
import { ApiError } from '@/api/client'
import {
  listBranchDocumentProfiles,
  updateBranchDocumentProfile,
  type BranchDocumentProfileRequest,
} from '@/api/fulfillment'

const EMPTY: BranchDocumentProfileRequest = { address: null, locality: null, phone: null, email: null, warehouseAddress: null }

const FIELDS: { key: keyof BranchDocumentProfileRequest; label: string; maxLength: number; type?: string }[] = [
  { key: 'address', label: 'documents.branch.address', maxLength: 200 },
  { key: 'locality', label: 'documents.branch.locality', maxLength: 120 },
  { key: 'phone', label: 'documents.branch.phone', maxLength: 60, type: 'tel' },
  { key: 'email', label: 'documents.branch.email', maxLength: 200, type: 'email' },
  { key: 'warehouseAddress', label: 'documents.branch.warehouse', maxLength: 200 },
]

/** A branch's data printed on its remitos: address, locality, phone, e-mail and warehouse (e.g. "Depósito Ruta 51"). */
export function BranchDocumentForm({ branchId, branchName, onBack }: { branchId: string; branchName: string; onBack: () => void }) {
  const { t } = useTranslation('fulfillment')
  const [form, setForm] = useState<BranchDocumentProfileRequest>(EMPTY)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)
  const [saving, setSaving] = useState(false)

  useEffect(() => {
    let cancelled = false
    listBranchDocumentProfiles()
      .then((profiles) => {
        const profile = profiles.find((item) => item.branchId === branchId)
        if (!cancelled && profile) {
          setForm({
            address: profile.address,
            locality: profile.locality,
            phone: profile.phone,
            email: profile.email,
            warehouseAddress: profile.warehouseAddress,
          })
        }
      })
      .catch(() => {
        if (!cancelled) setError(t('documents.loadError'))
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })
    return () => {
      cancelled = true
    }
  }, [branchId, t])

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    setSaving(true)
    setError(null)
    setSaved(false)
    try {
      await updateBranchDocumentProfile(branchId, form)
      setSaved(true)
    } catch (err) {
      const fieldErrors = err instanceof ApiError ? err.fieldErrors : undefined
      setError(fieldErrors ? Object.values(fieldErrors).flat().join(' ') : t('documents.saveError'))
    } finally {
      setSaving(false)
    }
  }

  return (
    <FormPage
      title={t('documents.branch.title', { name: branchName })}
      description={t('documents.branch.description')}
      onBack={onBack}
      backLabel={t('documents.branch.back')}
    >
      <form onSubmit={(event) => void submit(event)} className="grid max-w-2xl gap-4 sm:grid-cols-2" noValidate>
        {FIELDS.map((field) => (
          <div key={field.key} className={field.key === 'warehouseAddress' ? 'flex flex-col gap-1.5 sm:col-span-2' : 'flex flex-col gap-1.5'}>
            <Label htmlFor={`branch-${field.key}`}>{t(field.label)}</Label>
            <Input
              id={`branch-${field.key}`}
              type={field.type ?? 'text'}
              maxLength={field.maxLength}
              value={form[field.key] ?? ''}
              onChange={(e) => {
                setSaved(false)
                setForm((current) => ({ ...current, [field.key]: e.target.value === '' ? null : e.target.value }))
              }}
            />
          </div>
        ))}
        {error && (
          <p role="alert" className="text-sm text-destructive sm:col-span-2">
            {error}
          </p>
        )}
        {saved && (
          <p role="status" className="text-sm text-emerald-700 dark:text-emerald-400 sm:col-span-2">
            {t('documents.saved')}
          </p>
        )}
        <div className="flex gap-2 sm:col-span-2">
          <Button type="submit" disabled={loading || saving}>
            {saving ? t('documents.saving') : t('documents.save')}
          </Button>
          <Button type="button" variant="outline" onClick={onBack} disabled={saving}>
            {t('documents.branch.back')}
          </Button>
        </div>
      </form>
    </FormPage>
  )
}
