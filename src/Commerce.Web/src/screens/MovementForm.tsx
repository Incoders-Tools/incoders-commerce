import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Label } from '@/components/ui/label'
import { Select } from '@/components/ui/select'
import { FormPage } from '@/components/layout/FormPage'
import { Field, FormSection } from '@/components/form/FormParts'
import { DEBT_DIRECTION, registerMovement, type AccountPartyKind } from '@/api/currentAccount'
import { ApiError } from '@/api/client'
import { MovementKind, type MovementDirection, type RegisterMovementRequest } from '@/api/types'
import { todayIso } from '@/lib/isoDate'

/** Order the kinds are offered in. */
const KINDS: MovementKind[] = [
  MovementKind.OpeningBalance,
  MovementKind.Invoice,
  MovementKind.DebitNote,
  MovementKind.CreditNote,
  MovementKind.Payment,
  MovementKind.Adjustment,
]

/** Kinds that increase the debt (Credit on a supplier's account, Debit on a customer's); only those can have a due date. */
const INCREASES_DEBT: MovementKind[] = [MovementKind.OpeningBalance, MovementKind.Invoice, MovementKind.DebitNote]

interface MovementFormProps {
  party: AccountPartyKind
  partyId: string
  partyName: string
  /** Used to tell the operator when an empty due date defaults on an invoice. */
  paymentTermsDays: number | null
  onSaved: () => void
  onCancel: () => void
}

/** Positive, at most two decimals (the server's rule for `amount`). */
const isValidAmount = (value: string) => /^\d+(\.\d{1,2})?$/.test(value.trim()) && Number(value) > 0

/**
 * Full-page form to register one account movement. The operator picks what
 * happened (invoice, payment…) and the server derives its effect on the debt
 * from the kind; only an adjustment asks for the effect explicitly, in plain
 * words ("Aumenta deuda" / "Disminuye deuda"). Movements are append-only, so
 * the page says up front that a mistake is fixed by reversing, not editing.
 */
export function MovementForm({ party, partyId, partyName, paymentTermsDays, onSaved, onCancel }: MovementFormProps) {
  const { t } = useTranslation(`${party}Account`)
  const debt = DEBT_DIRECTION[party]
  const relief: MovementDirection = debt === 'Credit' ? 'Debit' : 'Credit'
  const [kind, setKind] = useState<MovementKind>(MovementKind.Invoice)
  // No preselected effect: a wrong default would silently book an adjustment the other way.
  const [direction, setDirection] = useState<MovementDirection | ''>('')
  const [amount, setAmount] = useState('')
  const [occurredOn, setOccurredOn] = useState(todayIso)
  const [dueOn, setDueOn] = useState('')
  const [documentReference, setDocumentReference] = useState('')
  const [concept, setConcept] = useState('')
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  const isAdjustment = kind === MovementKind.Adjustment
  const canHaveDueDate = INCREASES_DEBT.includes(kind) || (isAdjustment && direction === debt)
  const dueHint =
    kind === MovementKind.Invoice && paymentTermsDays !== null
      ? t('form.hints.dueOn', { days: paymentTermsDays })
      : t('form.hints.dueOnNoTerms')

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault()
    setError(null)
    const found: Record<string, string> = {}
    if (isAdjustment && direction === '') found.direction = t('form.errors.directionRequired')
    if (!isValidAmount(amount)) found.amount = t('form.errors.amountInvalid')
    if (concept.trim() === '') found.concept = t('form.errors.conceptRequired')
    if (canHaveDueDate && dueOn !== '' && occurredOn !== '' && dueOn < occurredOn) {
      found.dueOn = t('form.errors.dueBeforeDate')
    }
    setErrors(found)
    if (Object.keys(found).length > 0) return

    const request: RegisterMovementRequest = {
      kind,
      amount: Number(amount),
      concept: concept.trim(),
      ...(occurredOn !== '' ? { occurredOn } : {}),
      ...(canHaveDueDate && dueOn !== '' ? { dueOn } : {}),
      ...(documentReference.trim() !== '' ? { documentReference: documentReference.trim() } : {}),
      ...(isAdjustment && direction !== '' ? { direction } : {}),
    }
    setSubmitting(true)
    try {
      await registerMovement(party, partyId, request)
      onSaved()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : t('errors.unexpectedRegister'))
      setSubmitting(false)
    }
  }

  return (
    <FormPage
      title={t('form.title')}
      description={t('form.description', { name: partyName })}
      onBack={onCancel}
      backLabel={t('form.backLabel')}
    >
      <form className="flex flex-col gap-8" onSubmit={handleSubmit} noValidate>
        <FormSection title={t('form.title')}>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="movementKind">{t('form.fields.kind')}</Label>
            <Select
              id="movementKind"
              value={kind}
              onChange={(e) => setKind(e.target.value as MovementKind)}
              aria-describedby="movementKindHint"
            >
              {KINDS.map((option) => (
                <option key={option} value={option}>
                  {t(`kinds.${option}`)}
                </option>
              ))}
            </Select>
            <p id="movementKindHint" className="text-xs text-muted-foreground">
              {t(`kindHints.${kind}`)}
            </p>
          </div>

          {isAdjustment && (
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="movementDirection">{t('form.fields.direction')}</Label>
              <Select
                id="movementDirection"
                value={direction}
                onChange={(e) => setDirection(e.target.value as MovementDirection | '')}
                aria-invalid={errors.direction ? true : undefined}
                aria-describedby={errors.direction ? 'movementDirection-error' : undefined}
              >
                <option value="" disabled>
                  {t('form.directions.placeholder')}
                </option>
                <option value={debt}>{t(`form.directions.${debt}`)}</option>
                <option value={relief}>{t(`form.directions.${relief}`)}</option>
              </Select>
              {errors.direction && (
                <p id="movementDirection-error" className="text-xs text-destructive">
                  {errors.direction}
                </p>
              )}
            </div>
          )}

          <Field
            id="movementAmount"
            label={t('form.fields.amount')}
            value={amount}
            onChange={setAmount}
            type="number"
            step="0.01"
            hint={t('form.hints.amount')}
            error={errors.amount}
          />
          <Field
            id="movementOccurredOn"
            label={t('form.fields.occurredOn')}
            value={occurredOn}
            onChange={setOccurredOn}
            type="date"
          />
          {canHaveDueDate && (
            <Field
              id="movementDueOn"
              label={t('form.fields.dueOn')}
              value={dueOn}
              onChange={setDueOn}
              type="date"
              hint={dueHint}
              error={errors.dueOn}
            />
          )}
          <Field
            id="movementReference"
            label={t('form.fields.documentReference')}
            value={documentReference}
            onChange={setDocumentReference}
          />
          <Field
            id="movementConcept"
            label={t('form.fields.concept')}
            value={concept}
            onChange={setConcept}
            error={errors.concept}
            className="md:col-span-2"
          />
        </FormSection>

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
