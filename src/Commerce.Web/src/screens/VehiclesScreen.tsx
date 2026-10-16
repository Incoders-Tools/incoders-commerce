import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
import { DataView, type DataViewColumn } from '@/components/data/DataView'
import { PageHeader } from '@/components/data/PageHeader'
import { RowActions } from '@/components/data/RowActions'
import { FormPage } from '@/components/layout/FormPage'
import { ApiError } from '@/api/client'
import { createVehicle, listVehicles, updateVehicle, type Vehicle } from '@/api/vehicles'
import { cn } from '@/lib/utils'

const FIRST_YEAR = 1950

/** `VehicleRules.NormalizePlate` / `IsValidPlate`, mirrored: capitals without spaces or dashes, 5 to 10 letters or digits. */
const normalizePlate = (plate: string) => plate.replace(/[\s-]/g, '').toUpperCase()
const isValidPlate = (plate: string) => /^[A-Z0-9]{5,10}$/.test(plate)

/**
 * vehicles V2: the business's vehicles (one truck today) under the lookup tables. Listed by plate with the inactive ones,
 * created and edited (plate, brand, model, year, notes, active); a delivery run picks among the active ones.
 */
export function VehiclesScreen() {
  const { t } = useTranslation('fulfillment')
  const [vehicles, setVehicles] = useState<Vehicle[]>([])
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [status, setStatus] = useState<string | null>(null)
  const [editing, setEditing] = useState<{ vehicle: Vehicle | null } | null>(null)

  const load = useCallback(async () => {
    setLoading(true)
    setLoadError(null)
    try {
      setVehicles(await listVehicles(true))
    } catch {
      setLoadError(t('vehicles.errors.load'))
    } finally {
      setLoading(false)
    }
  }, [t])

  useEffect(() => {
    void load()
  }, [load])

  const columns: DataViewColumn<Vehicle>[] = [
    {
      key: 'plate',
      header: t('vehicles.columns.plate'),
      cell: (v) => <span className={cn('font-medium tabular-nums', !v.isActive && 'text-muted-foreground')}>{v.plate}</span>,
    },
    { key: 'vehicle', header: t('vehicles.columns.vehicle'), cell: (v) => [v.brand, v.model].filter(Boolean).join(' ') || '—' },
    { key: 'year', header: t('vehicles.columns.year'), cell: (v) => (v.year === null ? '—' : String(v.year)), hideOnMobile: true },
    { key: 'notes', header: t('vehicles.columns.notes'), cell: (v) => v.notes ?? '', hideOnMobile: true },
    {
      key: 'status',
      header: t('vehicles.columns.status'),
      cell: (v) => (v.isActive ? t('vehicles.active') : <span className="rounded bg-muted px-1.5 py-0.5 text-xs">{t('vehicles.inactive')}</span>),
    },
  ]

  if (editing) {
    return (
      <VehicleForm
        vehicle={editing.vehicle}
        onCancel={() => setEditing(null)}
        onSaved={(saved) => {
          setEditing(null)
          setStatus(t('vehicles.saved', { plate: saved.plate }))
          void load()
        }}
      />
    )
  }

  return (
    <section className="flex w-full flex-col gap-6">
      <PageHeader
        title={t('vehicles.title')}
        description={t('vehicles.description')}
        actions={
          <Button type="button" onClick={() => setEditing({ vehicle: null })}>
            {t('vehicles.create')}
          </Button>
        }
      />
      {status && (
        <p role="status" className="text-sm text-muted-foreground">
          {status}
        </p>
      )}
      <DataView
        items={vehicles}
        columns={columns}
        getRowKey={(v) => v.id}
        view="table"
        loading={loading}
        loadErrorMessage={loadError}
        emptyMessage={t('vehicles.empty')}
        renderActions={(vehicle) => (
          <RowActions label={vehicle.plate} actions={[{ key: 'edit', label: t('vehicles.edit'), onSelect: () => setEditing({ vehicle }) }]} />
        )}
      />
    </section>
  )
}

function VehicleForm({ vehicle, onCancel, onSaved }: { vehicle: Vehicle | null; onCancel: () => void; onSaved: (vehicle: Vehicle) => void }) {
  const { t } = useTranslation('fulfillment')
  const maxYear = new Date().getFullYear() + 1
  const [plate, setPlate] = useState(vehicle?.plate ?? '')
  const [brand, setBrand] = useState(vehicle?.brand ?? '')
  const [model, setModel] = useState(vehicle?.model ?? '')
  const [year, setYear] = useState(vehicle?.year == null ? '' : String(vehicle.year))
  const [notes, setNotes] = useState(vehicle?.notes ?? '')
  const [isActive, setIsActive] = useState(vehicle?.isActive ?? true)
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    if (!isValidPlate(normalizePlate(plate))) return setError(t('vehicles.errors.plate'))
    const parsedYear = year.trim() === '' ? null : /^\d{4}$/.test(year.trim()) ? Number(year.trim()) : NaN
    if (parsedYear !== null && !(parsedYear >= FIRST_YEAR && parsedYear <= maxYear)) {
      return setError(t('vehicles.errors.year', { first: FIRST_YEAR, last: maxYear }))
    }
    setError(null)
    setSaving(true)
    const request = {
      plate: plate.trim(),
      brand: brand.trim() || null,
      model: model.trim() || null,
      year: parsedYear,
      notes: notes.trim() || null,
    }
    try {
      onSaved(vehicle ? await updateVehicle(vehicle.id, { ...request, isActive }) : await createVehicle(request))
    } catch (err) {
      setError(
        err instanceof ApiError && err.code === 'vehicle-plate-in-use' ? t('vehicles.errors.plateInUse') : t('vehicles.errors.save'),
      )
    } finally {
      setSaving(false)
    }
  }

  return (
    <FormPage
      title={vehicle ? t('vehicles.editTitle', { plate: vehicle.plate }) : t('vehicles.createTitle')}
      description={t('vehicles.formDescription')}
      onBack={onCancel}
      backLabel={t('vehicles.back')}
    >
      <form className="flex max-w-2xl flex-col gap-4" onSubmit={(event) => void submit(event)} noValidate>
        <div className="grid gap-4 sm:grid-cols-2">
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="vehiclePlate">{t('vehicles.plate')}</Label>
            <Input id="vehiclePlate" maxLength={20} placeholder="AB 123 CD" value={plate} onChange={(e) => setPlate(e.target.value)} autoFocus />
            <p className="text-xs text-muted-foreground">{t('vehicles.plateHint')}</p>
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="vehicleYear">{t('vehicles.year')}</Label>
            <Input id="vehicleYear" inputMode="numeric" maxLength={4} placeholder="2015" value={year} onChange={(e) => setYear(e.target.value)} />
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="vehicleBrand">{t('vehicles.brand')}</Label>
            <Input id="vehicleBrand" maxLength={60} placeholder="Ford" value={brand} onChange={(e) => setBrand(e.target.value)} />
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="vehicleModel">{t('vehicles.model')}</Label>
            <Input id="vehicleModel" maxLength={60} placeholder="F-4000" value={model} onChange={(e) => setModel(e.target.value)} />
          </div>
        </div>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="vehicleNotes">{t('vehicles.notes')}</Label>
          <Textarea id="vehicleNotes" maxLength={500} rows={3} value={notes} onChange={(e) => setNotes(e.target.value)} />
        </div>
        {vehicle && (
          <label className="flex items-center gap-2 text-sm">
            <input type="checkbox" checked={isActive} onChange={(e) => setIsActive(e.target.checked)} />
            {t('vehicles.activeLabel')}
          </label>
        )}
        {error && (
          <p role="alert" className="text-sm text-destructive">
            {error}
          </p>
        )}
        <div className="flex gap-2">
          <Button type="submit" disabled={saving}>
            {saving ? t('vehicles.saving') : t('vehicles.save')}
          </Button>
          <Button type="button" variant="outline" onClick={onCancel}>
            {t('vehicles.cancel')}
          </Button>
        </div>
      </form>
    </FormPage>
  )
}
