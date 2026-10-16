import { useEffect, useState, type ChangeEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Building2 } from 'lucide-react'
import { listOrganizations } from '@/api/account'
import { ApiError } from '@/api/client'
import type { OrganizationSummary } from '@/api/types'
import { useAuth } from '@/auth/AuthContext'
import { Select } from '@/components/ui/select'
import { useOptionalOrganizationContext } from '@/organization/OrganizationContext'

const NO_ORGANIZATION_VALUE = ''

/**
 * platform-administration spec, "Sysadmin Acts On A Selected Organization":
 * lets a system administrator pick which organization every tenant module
 * (Customers, Users, Branches, Price lists, Catalog, Orders) acts on for
 * the rest of the session on this device. Rendered ONLY for a sysadmin
 * (`AppLayout` gates the mount) — a business-admin never sees this control.
 * "No organization" returns to the platform-only view (Organizations +
 * other platform screens, per the spec's "sees no tenant data" scenario).
 */
export function OrganizationSwitcher() {
  const { t } = useTranslation('nav')
  const { user } = useAuth()
  // Non-throwing: a host that renders `AppLayout` without an
  // `OrganizationProvider` mounted (e.g. an existing test) still renders —
  // there is simply nothing to select yet.
  const organizationContext = useOptionalOrganizationContext()
  const [organizations, setOrganizations] = useState<OrganizationSummary[]>([])
  const [loadError, setLoadError] = useState<string | null>(null)

  useEffect(() => {
    if (!user?.isSystemAdmin) return
    let cancelled = false
    listOrganizations()
      .then((result) => {
        if (!cancelled) setOrganizations(result)
      })
      .catch((err) => {
        if (!cancelled) setLoadError(err instanceof ApiError ? err.message : t('organizationSwitcher.loadError'))
      })
    return () => {
      cancelled = true
    }
  }, [user?.isSystemAdmin, t])

  if (!user?.isSystemAdmin || !organizationContext) return null

  const { selectedOrganization, selectOrganization, clearOrganization } = organizationContext

  const handleChange = (event: ChangeEvent<HTMLSelectElement>) => {
    const organizationId = event.target.value
    if (organizationId === NO_ORGANIZATION_VALUE) {
      clearOrganization()
      return
    }
    const organization = organizations.find((candidate) => candidate.id === organizationId)
    if (organization) {
      selectOrganization({ id: organization.id, name: organization.name })
    }
  }

  return (
    <div className="flex items-center gap-2">
      <Building2 aria-hidden="true" className="size-4 shrink-0 text-muted-foreground" />
      <label htmlFor="organization-switcher" className="sr-only">
        {t('organizationSwitcher.label')}
      </label>
      <Select
        id="organization-switcher"
        aria-label={t('organizationSwitcher.label')}
        value={selectedOrganization?.id ?? NO_ORGANIZATION_VALUE}
        onChange={handleChange}
        title={loadError ?? undefined}
        className="h-8 w-auto"
      >
        <option value={NO_ORGANIZATION_VALUE}>{t('organizationSwitcher.noOrganization')}</option>
        {organizations.map((organization) => (
          <option key={organization.id} value={organization.id}>
            {organization.name}
          </option>
        ))}
      </Select>
    </div>
  )
}
