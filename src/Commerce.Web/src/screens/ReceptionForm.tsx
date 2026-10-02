import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Label } from '@/components/ui/label'
import { Select } from '@/components/ui/select'
import { Textarea } from '@/components/ui/textarea'
import { FormPage } from '@/components/layout/FormPage'
import { ConfirmDialog } from '@/components/layout/ConfirmDialog'
import { CatalogSelect, Field, FormSection } from '@/components/form/FormParts'
import { usePresentationOptions } from '@/components/purchasing/presentationOptions'
import { confirmReception, createReception, getReception, updateReception } from '@/api/purchases'
import { ApiError } from '@/api/client'
import type {
  ReceptionDocumentType,
  ReceptionLineInput,
  ReceptionRecord,
  ReceptionRequest,
  SupplierRecord,
} from '@/api/types'
import { formatMoney } from '@/dashboard/format'
import { todayIso } from '@/lib/isoDate'
import { parseDecimal } from '@/lib/quantity'
import {
  ReceptionLinesEditor,
  newLineDraft,
  receptionTotal,
  type LineDraft,
  type LineErrors,
} from './ReceptionLinesEditor'

const DOCUMENT_TYPES: ReceptionDocumentType[] = ['Invoice', 'DeliveryNote', 'Other']

interface ReceptionFormProps {
  /** The stored draft being edited; absent for a new reception. */
  reception?: ReceptionRecord
  suppliers: SupplierRecord[]
  /** A new draft was stored for the first time: the screen moves to its own address. */
  onCreated: (reception: ReceptionRecord) => void
  /** Shows "Borrador guardado." on open (the screen just moved here after the first save). */
  savedNotice?: boolean
  /** The reception left the draft state (confirmed), or was reloaded after a conflict. */
  onChanged: (reception: ReceptionRecord) => void
  onBack: () => void
}

type Conflict = 'modified' | 'notDraft' | null

function draftsFromReception(reception?: ReceptionRecord): LineDraft[] {
  if (!reception || reception.lines.length === 0) return [newLineDraft()]
  return [...reception.lines]
    .sort((a, b) => a.sortOrder - b.sortOrder)
    .map((line) => ({
      ...newLineDraft(),
      presentation: {
        id: line.presentationId,
        productName: line.productName,
        presentationName: line.presentationName,
        behavior: line.quantityBehavior,
        identificationCode: null,
      },
      quantity: String(line.quantity).replace('.', ','),
      unitCost: String(line.unitCost).replace('.', ','),
      lotCode: line.lotCode ?? '',
      expiresOn: line.expiresOn ?? '',
    }))
}

/** Splits the server's `lines[i].field` keys into per-line errors and the rest. */
function splitServerErrors(fieldErrors: Record<string, string[]> | undefined) {
  const lineErrors: Record<number, LineErrors> = {}
  const general: string[] = []
  for (const [key, messages] of Object.entries(fieldErrors ?? {})) {
    const match = /^lines\[(\d+)\]\.(\w+)$/.exec(key)
    if (match) {
      const field = match[2] as keyof LineErrors
      lineErrors[Number(match[1])] = { ...lineErrors[Number(match[1])], [field]: messages[0] }
    } else {
      general.push(...messages)
    }
  }
  return { lineErrors, general }
}

/**
 * Full-page form of a draft goods reception (new, or an existing draft).
 * Client validation mirrors the server's rules so the operator hears about a
 * missing supplier or quantity before the round trip; the server stays the
 * authority and its field errors are shown next to the line they belong to.
 * Confirming first saves the draft, then asks for confirmation because it moves
 * stock and posts an invoice to the supplier's account.
 */
export function ReceptionForm({ reception, suppliers, onCreated, savedNotice = false, onChanged, onBack }: ReceptionFormProps) {
  const { t } = useTranslation('purchases')
  const { options, failed: optionsFailed } = usePresentationOptions()
  const [record, setRecord] = useState<ReceptionRecord | undefined>(reception)
  const [supplierId, setSupplierId] = useState(reception?.supplierId ?? '')
  const [documentType, setDocumentType] = useState<ReceptionDocumentType>(reception?.documentType ?? 'Invoice')
  const [documentReference, setDocumentReference] = useState(reception?.documentReference ?? '')
  const [occurredOn, setOccurredOn] = useState(reception?.occurredOn ?? todayIso())
  const [dueOn, setDueOn] = useState(reception?.dueOn ?? '')
  const [notes, setNotes] = useState(reception?.notes ?? '')
  const [lines, setLines] = useState<LineDraft[]>(() => draftsFromReception(reception))
  const [supplierError, setSupplierError] = useState<string | null>(null)
  const [lineErrors, setLineErrors] = useState<Record<number, LineErrors>>({})
  const [formErrors, setFormErrors] = useState<string[]>([])
  const [conflict, setConflict] = useState<Conflict>(null)
  const [notice, setNotice] = useState<string | null>(savedNotice ? t('form.saved') : null)
  const [busy, setBusy] = useState(false)
  const [confirming, setConfirming] = useState(false)

  const supplierOptions = suppliers.map((supplier) => ({ id: supplier.id, name: supplier.displayName }))
  if (reception && !supplierOptions.some((option) => option.id === reception.supplierId)) {
    supplierOptions.push({ id: reception.supplierId, name: reception.supplierName })
  }

  const resetMessages = () => {
    setSupplierError(null)
    setLineErrors({})
    setFormErrors([])
    setConflict(null)
    setNotice(null)
  }

  /** Validates the form and builds the request, or shows what is wrong and returns null. */
  const buildRequest = (): ReceptionRequest | null => {
    resetMessages()
    const errors: Record<number, LineErrors> = {}
    const inputs: ReceptionLineInput[] = []
    lines.forEach((line, index) => {
      const lineError: LineErrors = {}
      const quantity = parseDecimal(line.quantity)
      const unitCost = parseDecimal(line.unitCost)
      if (!line.presentation) lineError.presentationId = t('lines.errors.presentationRequired')
      if (quantity === null || quantity <= 0) lineError.quantity = t('lines.errors.quantityRequired')
      else if (line.presentation?.behavior === 'FixedQuantity' && !Number.isInteger(quantity)) {
        lineError.quantity = t('lines.errors.quantityInteger')
      }
      if (unitCost === null || unitCost < 0) lineError.unitCost = t('lines.errors.unitCostRequired')
      if (Object.keys(lineError).length > 0) errors[index] = lineError
      else {
        inputs.push({
          presentationId: line.presentation!.id,
          quantity: quantity!,
          unitCost: unitCost!,
          lotCode: line.lotCode.trim() || null,
          expiresOn: line.expiresOn || null,
        })
      }
    })
    const missingSupplier = supplierId === ''
    if (missingSupplier) setSupplierError(t('form.errors.supplierRequired'))
    setLineErrors(errors)
    if (missingSupplier || Object.keys(errors).length > 0) return null
    return {
      supplierId,
      documentType,
      documentReference: documentReference.trim() || null,
      occurredOn: occurredOn || null,
      dueOn: dueOn || null,
      notes: notes.trim() || null,
      lines: inputs,
    }
  }

  const showApiError = (err: unknown, fallback: string) => {
    if (err instanceof ApiError) {
      if (err.status === 409 && err.code === 'reception-modified') return setConflict('modified')
      if (err.status === 409 && err.code === 'reception-not-draft') return setConflict('notDraft')
      if (err.status === 409 && err.code === 'reception-duplicate-document') {
        return setFormErrors([t('form.errors.duplicateDocument')])
      }
      if (err.code === 'branch-selection-required') return setFormErrors([t('branchRequired')])
      if (err.status === 400 && err.fieldErrors) {
        const split = splitServerErrors(err.fieldErrors)
        setLineErrors(split.lineErrors)
        if (split.general.length > 0) setFormErrors(split.general)
        if (Object.keys(split.lineErrors).length > 0 || split.general.length > 0) return
      }
      return setFormErrors([err.message])
    }
    setFormErrors([fallback])
  }

  /** Creates or replaces the draft. Returns the stored record, or null after showing why it failed. */
  const persist = async (request: ReceptionRequest): Promise<ReceptionRecord | null> => {
    try {
      const saved = record
        ? await updateReception(record.id, { ...request, expectedUpdatedAtUtc: record.updatedAtUtc })
        : await createReception(request)
      setRecord(saved)
      return saved
    } catch (err) {
      showApiError(err, t('form.errors.unexpectedSave'))
      return null
    }
  }

  const saveDraft = async () => {
    const request = buildRequest()
    if (!request) return
    setBusy(true)
    try {
      const created = record === undefined
      const saved = await persist(request)
      if (!saved) return
      if (created) onCreated(saved)
      else setNotice(t('form.saved'))
    } finally {
      setBusy(false)
    }
  }

  const startConfirm = () => {
    if (buildRequest()) setConfirming(true)
  }

  const confirm = async () => {
    const request = buildRequest()
    if (!request) {
      setConfirming(false)
      return
    }
    setBusy(true)
    try {
      const saved = await persist(request)
      if (!saved) return
      try {
        onChanged(await confirmReception(saved.id))
      } catch (err) {
        showApiError(err, t('form.errors.unexpectedSave'))
      }
    } finally {
      setBusy(false)
      setConfirming(false)
    }
  }

  const reload = async () => {
    if (!record) return
    try {
      const fresh = await getReception(record.id)
      resetMessages()
      onChanged(fresh)
    } catch {
      setConflict(null)
      setFormErrors([t('form.conflict.reloadFailed')])
    }
  }

  return (
    <FormPage
      title={record ? t('form.titleEdit') : t('form.titleNew')}
      onBack={onBack}
      backLabel={t('form.backLabel')}
    >
      {record && (
        <p className="-mt-3 mb-4">
          <span className="inline-flex rounded-full border border-border bg-muted px-2 py-0.5 text-xs font-medium text-muted-foreground">
            {t('status.Draft')}
          </span>
        </p>
      )}
      <form
        className="flex flex-col gap-8"
        onSubmit={(event) => {
          event.preventDefault()
          void saveDraft()
        }}
      >
        <FormSection title={t('form.sections.document')}>
          <CatalogSelect
            id="supplierId"
            label={t('form.fields.supplier')}
            emptyLabel={t('form.noSupplier')}
            value={supplierId}
            onChange={(value) => {
              setSupplierId(value)
              setSupplierError(null)
            }}
            options={supplierOptions}
            error={supplierError}
          />
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="documentType">{t('form.fields.documentType')}</Label>
            <Select
              id="documentType"
              value={documentType}
              onChange={(e) => setDocumentType(e.target.value as ReceptionDocumentType)}
            >
              {DOCUMENT_TYPES.map((type) => (
                <option key={type} value={type}>
                  {t(`documentType.${type}`)}
                </option>
              ))}
            </Select>
          </div>
          <Field
            id="documentReference"
            label={t('form.fields.documentReference')}
            value={documentReference}
            onChange={setDocumentReference}
          />
          <Field id="occurredOn" type="date" label={t('form.fields.occurredOn')} value={occurredOn} onChange={setOccurredOn} />
          <Field
            id="dueOn"
            type="date"
            label={t('form.fields.dueOn')}
            hint={t('form.fields.dueOnHint')}
            value={dueOn}
            onChange={setDueOn}
          />
        </FormSection>

        <FormSection title={t('form.sections.lines')} wide>
          <ReceptionLinesEditor
            lines={lines}
            options={options}
            optionsFailed={optionsFailed}
            errors={lineErrors}
            onChange={setLines}
          />
          <p data-testid="reception-total" className="text-right text-base font-semibold tabular-nums">
            {t('form.total')}: {formatMoney(receptionTotal(lines))}
          </p>
        </FormSection>

        <FormSection title={t('form.sections.notes')} wide>
          <Label htmlFor="notes" className="sr-only">
            {t('form.fields.notes')}
          </Label>
          <Textarea id="notes" rows={3} value={notes} onChange={(e) => setNotes(e.target.value)} />
        </FormSection>

        {conflict && (
          <div
            role="alert"
            className="flex flex-wrap items-center gap-3 rounded-md border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive"
          >
            <p>{t(conflict === 'modified' ? 'form.conflict.modified' : 'form.conflict.notDraft')}</p>
            <Button type="button" variant="outline" size="sm" onClick={() => void reload()}>
              {t('form.conflict.reload')}
            </Button>
          </div>
        )}
        {formErrors.map((message) => (
          <p key={message} role="alert" className="text-sm text-destructive">
            {message}
          </p>
        ))}
        {notice && (
          <p role="status" className="text-sm text-emerald-700 dark:text-emerald-400">
            {notice}
          </p>
        )}

        <div className="flex flex-wrap gap-2">
          <Button type="submit" variant="outline" disabled={busy}>
            {busy ? t('form.saving') : t('form.saveDraft')}
          </Button>
          <Button type="button" onClick={startConfirm} disabled={busy}>
            {t('form.confirm')}
          </Button>
          <Button type="button" variant="outline" onClick={onBack} disabled={busy}>
            {t('form.cancel')}
          </Button>
        </div>
      </form>

      {confirming && (
        <ConfirmDialog
          title={t('confirmDialog.title')}
          message={t('confirmDialog.message')}
          confirmLabel={t('confirmDialog.confirm')}
          busyLabel={t('confirmDialog.confirming')}
          busy={busy}
          onConfirm={() => void confirm()}
          onCancel={() => setConfirming(false)}
        />
      )}
    </FormPage>
  )
}
