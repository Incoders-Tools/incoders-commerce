import { useEffect, useState } from 'react'
import { useLocation, useNavigate, useParams } from 'react-router'
import { useTranslation } from 'react-i18next'
import { PageHeader } from '@/components/data/PageHeader'
import { getReception } from '@/api/purchases'
import { listSuppliers } from '@/api/suppliers'
import { ApiError } from '@/api/client'
import type { ReceptionRecord, SupplierRecord } from '@/api/types'
import { useMissingBranch } from '@/branch/useMissingBranch'
import { ReceptionForm } from './ReceptionForm'
import { ReceptionView } from './ReceptionView'

interface NavigationState {
  reception?: ReceptionRecord
  savedNotice?: boolean
}

/**
 * Route screen of one reception: `/app/receptions/new` opens an empty draft
 * form; `/app/receptions/:id` opens a draft for editing and a confirmed or
 * voided reception read only. A draft that was just created arrives through the
 * navigation state, so showing it costs no second request.
 */
export function ReceptionScreen() {
  const { id } = useParams()
  // `new` and `:id` render this same component at the same place in the tree: key it so each address starts fresh.
  return <ReceptionRoute key={id ?? 'new'} id={id} />
}

function ReceptionRoute({ id }: { id: string | undefined }) {
  const { t } = useTranslation('purchases')
  const navigate = useNavigate()
  const location = useLocation()
  const missingBranch = useMissingBranch()
  const arrived = (location.state as NavigationState | null) ?? null
  const [reception, setReception] = useState<ReceptionRecord | null>(
    arrived?.reception && arrived.reception.id === id ? arrived.reception : null,
  )
  const [suppliers, setSuppliers] = useState<SupplierRecord[]>([])
  const [loading, setLoading] = useState(id !== undefined && reception === null && !missingBranch)
  const [loadError, setLoadError] = useState<string | null>(null)

  useEffect(() => {
    if (missingBranch) return
    listSuppliers({ enabled: true }).then(setSuppliers, () => setSuppliers([]))
  }, [missingBranch])

  useEffect(() => {
    if (missingBranch || !id || reception !== null) return
    let current = true
    getReception(id).then(
      (loaded) => {
        if (!current) return
        setReception(loaded)
        setLoading(false)
      },
      (err: unknown) => {
        if (!current) return
        setLoadError(
          err instanceof ApiError
            ? err.status === 404
              ? t('form.errors.notFound')
              : err.message
            : t('form.errors.unexpectedLoad'),
        )
        setLoading(false)
      },
    )
    return () => {
      current = false
    }
  }, [id, reception, missingBranch, t])

  const goBack = () => navigate('/app/receptions')

  if (missingBranch) {
    return (
      <section className="flex w-full flex-col gap-6">
        <PageHeader title={t('form.titleNew')} />
        <p role="status" className="rounded-lg border border-border bg-card px-4 py-10 text-center text-sm text-muted-foreground">
          {t('branchRequired')}
        </p>
      </section>
    )
  }

  if (loading) {
    return (
      <p role="status" className="rounded-lg border border-border bg-card px-4 py-10 text-center text-sm text-muted-foreground">
        {t('form.saving')}
      </p>
    )
  }

  if (loadError) {
    return (
      <section className="flex w-full flex-col gap-4">
        <p role="alert" className="rounded-md border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {loadError}
        </p>
        <button type="button" className="w-fit text-sm text-primary underline" onClick={goBack}>
          {t('form.backLabel')}
        </button>
      </section>
    )
  }

  if (reception && reception.status !== 'Draft') {
    return <ReceptionView key={reception.updatedAtUtc} reception={reception} onChanged={setReception} onBack={goBack} />
  }

  return (
    <ReceptionForm
      // A reload or a confirmation swaps in fresher data: remount so the form restarts from it.
      key={reception ? `${reception.id}:${reception.updatedAtUtc}` : 'new'}
      reception={reception ?? undefined}
      suppliers={suppliers}
      savedNotice={arrived?.savedNotice === true}
      onCreated={(created) =>
        navigate(`/app/receptions/${created.id}`, { replace: true, state: { reception: created, savedNotice: true } })
      }
      onChanged={setReception}
      onBack={goBack}
    />
  )
}
