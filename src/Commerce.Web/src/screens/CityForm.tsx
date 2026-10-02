import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select } from '@/components/ui/select'
import { FormPage } from '@/components/layout/FormPage'
import { ApiError } from '@/api/client'
import { createCity, updateCity } from '@/api/geo'
import type { GeoCity, GeoProvince } from '@/api/types'

const dateFormat = new Intl.DateTimeFormat('es-AR', { day: '2-digit', month: '2-digit', year: 'numeric' })

interface CityFormProps {
  city: GeoCity | null
  provinces: GeoProvince[]
  onCancel: () => void
  onSaved: () => void
}

/**
 * Full-page create/edit form for the core city catalog (system administrator
 * only; the server answers 403 to anyone else). On edit the department is
 * always sent: a blank value is the server's "clear it" signal, an omitted
 * one would keep the stored value.
 */
export function CityForm({ city, provinces, onCancel, onSaved }: CityFormProps) {
  const { t } = useTranslation('cities')
  const [name, setName] = useState(city?.name ?? '')
  const [provinceId, setProvinceId] = useState(city?.provinceId ?? '')
  const [departmentName, setDepartmentName] = useState(city?.departmentName ?? '')
  const [isActive, setIsActive] = useState(city?.isActive ?? true)
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault()
    setError(null)
    setSubmitting(true)
    try {
      const request = { name: name.trim(), provinceId, departmentName: departmentName.trim(), isActive }
      if (city) {
        await updateCity(city.id, request)
      } else {
        const { departmentName: department, ...rest } = request
        await createCity(department === '' ? rest : request)
      }
      onSaved()
    } catch (err) {
      setError(saveErrorMessage(err, t))
      setSubmitting(false)
    }
  }

  return (
    <FormPage
      title={city ? t('form.editTitle') : t('form.createTitle')}
      description={t('form.description')}
      onBack={onCancel}
      backLabel={t('form.backLabel')}
    >
      <form className="flex flex-col gap-6" onSubmit={handleSubmit}>
        <div className="grid grid-cols-1 gap-x-6 gap-y-4 md:grid-cols-2 xl:grid-cols-3">
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="cityName">{t('form.name')}</Label>
            <Input id="cityName" value={name} onChange={(e) => setName(e.target.value)} required />
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="cityProvince">{t('form.province')}</Label>
            <Select id="cityProvince" value={provinceId} onChange={(e) => setProvinceId(e.target.value)} required>
              <option value="">{t('form.provincePlaceholder')}</option>
              {provinces.map((province) => (
                <option key={province.id} value={province.id}>
                  {province.name}
                </option>
              ))}
            </Select>
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="cityDepartment">{t('form.department')}</Label>
            <Input id="cityDepartment" value={departmentName} onChange={(e) => setDepartmentName(e.target.value)} />
          </div>
          <div className="flex items-center gap-2">
            <input id="cityIsActive" type="checkbox" checked={isActive} onChange={(e) => setIsActive(e.target.checked)} />
            <Label htmlFor="cityIsActive">{t('form.isActive')}</Label>
          </div>
          {city && (
            <div className="flex flex-col gap-1 text-sm text-muted-foreground md:col-span-2">
              {city.indecId && <p>{t('form.indecId', { code: city.indecId })}</p>}
              <p>{t('form.updatedAt', { date: dateFormat.format(new Date(city.updatedAtUtc)) })}</p>
            </div>
          )}
        </div>

        {error && (
          <p role="alert" className="text-sm text-destructive">
            {error}
          </p>
        )}
        <div className="flex gap-2">
          <Button type="submit" disabled={submitting}>
            {submitting ? t('form.saving') : t('form.save')}
          </Button>
          <Button type="button" variant="outline" onClick={onCancel} disabled={submitting}>
            {t('form.cancel')}
          </Button>
        </div>
      </form>
    </FormPage>
  )
}

function saveErrorMessage(err: unknown, t: (key: string) => string): string {
  if (err instanceof ApiError) {
    if (err.status === 409 && err.code === 'city-name-in-use') return t('errors.nameInUse')
    if (err.status === 403) return t('errors.forbidden')
    if (err.status === 400) return t('errors.invalid')
  }
  return t('errors.unableToSave')
}
