import { useState, type FormEvent } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { PageHeader } from '@/components/data/PageHeader'
import { createOrganization } from '@/api/account'
import { ApiError } from '@/api/client'

interface OrganizationFormProps {
  onCreated: () => void
  onCancel: () => void
}

/**
 * T8: full-width replacement of the organizations list — same state-swap
 * pattern `CustomersScreen` uses for `CustomerForm` — rendered behind the
 * "New organization" action instead of the previous always-visible,
 * unformatted inline form. `branchName` was already optional on
 * `CreateOrganizationRequest`; the pre-T8 screen just hardcoded it to
 * `'Main'` without exposing a field. Settings fields (logo, theme colors,
 * date format, geolocation, usage plan) stay out of scope here — T5 owns
 * them once a spec exists.
 */
export function OrganizationForm({ onCreated, onCancel }: OrganizationFormProps) {
  const [organizationName, setOrganizationName] = useState('')
  const [branchName, setBranchName] = useState('')
  const [adminEmail, setAdminEmail] = useState('')
  const [adminPassword, setAdminPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault()
    setError(null)
    setSubmitting(true)
    try {
      await createOrganization({
        organizationName,
        branchName: branchName === '' ? null : branchName,
        adminEmail,
        adminPassword,
      })
      onCreated()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Unable to create organization.')
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <section className="flex w-full flex-col gap-6">
      <PageHeader
        title="New organization"
        description="Onboard a new tenant with its first branch and administrator."
      />

      <form onSubmit={handleSubmit} className="flex flex-col gap-6">
        <div className="grid grid-cols-1 gap-x-6 gap-y-4 md:grid-cols-2">
          <Field
            id="organizationName"
            label="Organization name"
            value={organizationName}
            onChange={setOrganizationName}
            required
          />
          <Field id="branchName" label="Branch name" value={branchName} onChange={setBranchName} />
          <Field
            id="adminEmail"
            label="Administrator email"
            type="email"
            value={adminEmail}
            onChange={setAdminEmail}
            required
          />
          <Field
            id="adminPassword"
            label="Administrator password"
            type="password"
            value={adminPassword}
            onChange={setAdminPassword}
            required
          />
        </div>

        {error && (
          <p role="alert" className="text-sm text-destructive">
            {error}
          </p>
        )}

        <div className="flex gap-2">
          <Button type="submit" disabled={submitting}>
            {submitting ? 'Creating…' : 'Create organization'}
          </Button>
          <Button type="button" variant="outline" onClick={onCancel} disabled={submitting}>
            Cancel
          </Button>
        </div>
      </form>
    </section>
  )
}

function Field({
  id,
  label,
  value,
  onChange,
  type = 'text',
  required = false,
}: {
  id: string
  label: string
  value: string
  onChange: (value: string) => void
  type?: string
  required?: boolean
}) {
  return (
    <div className="flex flex-col gap-1.5">
      <Label htmlFor={id}>{label}</Label>
      <Input id={id} type={type} value={value} onChange={(e) => onChange(e.target.value)} required={required} />
    </div>
  )
}
