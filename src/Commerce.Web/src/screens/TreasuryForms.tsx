import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select } from '@/components/ui/select'
import { ApiError } from '@/api/client'
import type { MasterDataEntry } from '@/api/types'
import {
  createTreasuryAccount,
  editTreasuryMovement,
  recordTreasuryMovement,
  transferBetweenAccounts,
  updateTreasuryAccount,
  type EditTreasuryMovementRequest,
  type TreasuryAccount,
  type TreasuryMovement,
} from '@/api/treasury'
import { accountLabel, baseConcept, isoDay, parseAmount, type BranchOption } from '@/treasury/treasuryInput'

/** The message of a refused write: the known machine codes in plain words, else what the server said. */
function useErrorText() {
  const { t } = useTranslation('treasury')
  return (err: unknown) =>
    err instanceof ApiError && err.code
      ? t(`errors.codes.${err.code}`, { defaultValue: err.message })
      : err instanceof ApiError
        ? err.message
        : t('errors.save')
}

function FormShell({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="flex flex-col gap-4 rounded-lg border border-border bg-card p-4">
      <h2 className="text-lg font-semibold">{title}</h2>
      {children}
    </div>
  )
}

function ErrorLine({ error }: { error: string | null }) {
  return error ? (
    <p role="alert" className="text-sm text-destructive">
      {error}
    </p>
  ) : null
}

/** Creates an account, or edits one (name, type, description; its branch and role never change). */
export function AccountForm({
  account,
  types,
  branches,
  onSaved,
  onCancel,
}: {
  /** The account to edit; omitted to create one. */
  account?: TreasuryAccount
  types: readonly MasterDataEntry[]
  branches: readonly BranchOption[]
  onSaved: (account: TreasuryAccount) => void
  onCancel: () => void
}) {
  const { t } = useTranslation('treasury')
  const errorText = useErrorText()
  const editing = account !== undefined
  const [name, setName] = useState(account?.name ?? '')
  const [typeId, setTypeId] = useState(account?.accountTypeId ?? types.find((type) => type.isActive)?.id ?? '')
  const [isSafe, setIsSafe] = useState(false)
  const [branchId, setBranchId] = useState('')
  const [description, setDescription] = useState(account?.description ?? '')
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)

  // Active types, plus the account's current one even if it was deactivated since.
  const typeOptions = types.filter((type) => type.isActive || type.id === account?.accountTypeId)

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    if (name.trim() === '') return setError(t('accountForm.errors.name'))
    if (isSafe && branchId === '') return setError(t('accountForm.errors.safeBranch'))
    setError(null)
    setSaving(true)
    try {
      const saved = editing
        ? await updateTreasuryAccount(account.accountId, {
            name: name.trim(),
            accountTypeId: typeId || null,
            description: description.trim() || null,
          })
        : await createTreasuryAccount({
            name: name.trim(),
            ...(isSafe ? { kind: 'Safe' as const } : {}),
            ...(typeId ? { accountTypeId: typeId } : {}),
            ...(branchId ? { branchId } : {}),
            ...(description.trim() ? { description: description.trim() } : {}),
          })
      onSaved(saved)
    } catch (err) {
      setError(errorText(err))
    } finally {
      setSaving(false)
    }
  }

  return (
    <FormShell title={editing ? t('accountForm.editTitle') : t('accountForm.title')}>
      <form onSubmit={(event) => void submit(event)} className="flex flex-col gap-4" noValidate>
        <div className="grid gap-4 sm:grid-cols-2">
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="treasuryAccountName">{t('accountForm.name')}</Label>
            <Input
              id="treasuryAccountName"
              value={name}
              maxLength={120}
              placeholder={t('accountForm.namePlaceholder')}
              onChange={(e) => setName(e.target.value)}
            />
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="treasuryAccountType">{t('accountForm.type')}</Label>
            <Select id="treasuryAccountType" value={typeId} onChange={(e) => setTypeId(e.target.value)}>
              <option value="">{t('accountForm.noType')}</option>
              {typeOptions.map((type) => (
                <option key={type.id} value={type.id}>
                  {type.name}
                </option>
              ))}
            </Select>
            <p className="text-xs text-muted-foreground">{t('accountForm.typeHint')}</p>
          </div>
          {!editing && (
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="treasuryAccountBranch">{t('accountForm.branch')}</Label>
              <Select id="treasuryAccountBranch" value={branchId} onChange={(e) => setBranchId(e.target.value)}>
                <option value="">{isSafe ? t('accountForm.chooseBranch') : t('accountForm.wholeCompany')}</option>
                {branches.map((branch) => (
                  <option key={branch.id} value={branch.id}>
                    {branch.name}
                  </option>
                ))}
              </Select>
              <label className="flex items-center gap-2 text-sm">
                <input type="checkbox" checked={isSafe} onChange={(e) => setIsSafe(e.target.checked)} />
                {t('accountForm.isSafe')}
              </label>
              <p className="text-xs text-muted-foreground">{t('accountForm.isSafeHint')}</p>
            </div>
          )}
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="treasuryAccountDescription">{t('accountForm.description')}</Label>
            <Input
              id="treasuryAccountDescription"
              value={description}
              maxLength={200}
              placeholder={t('accountForm.descriptionPlaceholder')}
              onChange={(e) => setDescription(e.target.value)}
            />
          </div>
        </div>
        <ErrorLine error={error} />
        <div className="flex gap-2">
          <Button type="submit" disabled={saving}>
            {saving ? t('saving') : editing ? t('accountForm.save') : t('accountForm.create')}
          </Button>
          <Button type="button" variant="outline" onClick={onCancel}>
            {t('cancel')}
          </Button>
        </div>
      </form>
    </FormShell>
  )
}

type MovementType = 'In' | 'Out' | 'Transfer'

/** Money In or Out of an account by hand, or a transfer between two accounts (active accounts only). */
export function MovementForm({
  accounts,
  initialAccountId,
  onSaved,
  onCancel,
}: {
  accounts: readonly TreasuryAccount[]
  initialAccountId: string | null
  onSaved: (accountId: string) => void
  onCancel: () => void
}) {
  const { t } = useTranslation('treasury')
  const errorText = useErrorText()
  const initial = accounts.some((a) => a.accountId === initialAccountId) ? initialAccountId! : (accounts[0]?.accountId ?? '')
  const [type, setType] = useState<MovementType>('In')
  const [accountId, setAccountId] = useState(initial)
  const [toAccountId, setToAccountId] = useState(accounts.find((a) => a.accountId !== initial)?.accountId ?? '')
  const [amount, setAmount] = useState('')
  const [date, setDate] = useState(() => isoDay(new Date()))
  const [concept, setConcept] = useState('')
  const [reference, setReference] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const wholeCompany = t('wholeCompany')

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    const value = parseAmount(amount)
    if (value === null) return setError(t('movementForm.errors.amount'))
    if (concept.trim() === '') return setError(t('movementForm.errors.concept'))
    if (type === 'Transfer' && accountId === toAccountId) return setError(t('movementForm.errors.sameAccount'))
    if (date > isoDay(new Date())) return setError(t('movementForm.errors.future'))
    setError(null)
    setSaving(true)
    try {
      const shared = { amount: value, date, concept: concept.trim(), ...(reference.trim() ? { reference: reference.trim() } : {}) }
      if (type === 'Transfer') {
        await transferBetweenAccounts({ fromAccountId: accountId, toAccountId, ...shared })
      } else {
        await recordTreasuryMovement({ accountId, direction: type, ...shared })
      }
      onSaved(accountId)
    } catch (err) {
      setError(errorText(err))
    } finally {
      setSaving(false)
    }
  }

  const accountOptions = accounts.map((account) => (
    <option key={account.accountId} value={account.accountId}>
      {accountLabel(account, wholeCompany)}
    </option>
  ))

  return (
    <FormShell title={t('movementForm.title')}>
      <form onSubmit={(event) => void submit(event)} className="flex flex-col gap-4" noValidate>
        <div className="grid gap-4 sm:grid-cols-2">
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="treasuryMovementType">{t('movementForm.type')}</Label>
            <Select id="treasuryMovementType" value={type} onChange={(e) => setType(e.target.value as MovementType)}>
              <option value="In">{t('movementForm.types.In')}</option>
              <option value="Out">{t('movementForm.types.Out')}</option>
              <option value="Transfer">{t('movementForm.types.Transfer')}</option>
            </Select>
            <p className="text-xs text-muted-foreground">{t(`movementForm.typeHint.${type}`)}</p>
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="treasuryMovementAccount">
              {type === 'Transfer' ? t('movementForm.fromAccount') : t('movementForm.account')}
            </Label>
            <Select id="treasuryMovementAccount" value={accountId} onChange={(e) => setAccountId(e.target.value)}>
              {accountOptions}
            </Select>
          </div>
          {type === 'Transfer' && (
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="treasuryMovementToAccount">{t('movementForm.toAccount')}</Label>
              <Select id="treasuryMovementToAccount" value={toAccountId} onChange={(e) => setToAccountId(e.target.value)}>
                {accountOptions}
              </Select>
            </div>
          )}
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="treasuryMovementAmount">{t('movementForm.amount')}</Label>
            <Input
              id="treasuryMovementAmount"
              inputMode="decimal"
              value={amount}
              placeholder={t('movementForm.amountPlaceholder')}
              onChange={(e) => setAmount(e.target.value)}
            />
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="treasuryMovementDate">{t('movementForm.date')}</Label>
            <Input id="treasuryMovementDate" type="date" value={date} max={isoDay(new Date())} onChange={(e) => setDate(e.target.value)} />
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="treasuryMovementConcept">{t('movementForm.concept')}</Label>
            <Input
              id="treasuryMovementConcept"
              value={concept}
              maxLength={200}
              placeholder={t(`movementForm.conceptPlaceholder.${type}`)}
              onChange={(e) => setConcept(e.target.value)}
            />
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="treasuryMovementReference">{t('movementForm.reference')}</Label>
            <Input
              id="treasuryMovementReference"
              value={reference}
              maxLength={60}
              placeholder={t('movementForm.referencePlaceholder')}
              onChange={(e) => setReference(e.target.value)}
            />
          </div>
        </div>
        <ErrorLine error={error} />
        <div className="flex gap-2">
          <Button type="submit" disabled={saving || accounts.length === 0}>
            {saving ? t('saving') : t('movementForm.save')}
          </Button>
          <Button type="button" variant="outline" onClick={onCancel}>
            {t('cancel')}
          </Button>
        </div>
      </form>
    </FormShell>
  )
}

/**
 * Edits a movement: the original stays, voided, and a corrected one replaces it. A sale or customer payment only moves
 * to another account; a transfer keeps its accounts (void it and record it again to change them).
 */
export function MovementEditForm({
  movement,
  accounts,
  onSaved,
  onCancel,
}: {
  movement: TreasuryMovement
  accounts: readonly TreasuryAccount[]
  onSaved: () => void
  onCancel: () => void
}) {
  const { t } = useTranslation('treasury')
  const errorText = useErrorText()
  const onlyAccount = movement.canReclassify === true
  const isTransfer = movement.kind === 'Transfer'
  const [accountId, setAccountId] = useState(movement.accountId)
  const [amount, setAmount] = useState(String(movement.amount).replace('.', ','))
  const [date, setDate] = useState(movement.businessDate.slice(0, 10))
  const [concept, setConcept] = useState(baseConcept(movement))
  const [reference, setReference] = useState(movement.documentReference ?? '')
  const [reason, setReason] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const wholeCompany = t('wholeCompany')

  // The account list offers active accounts, plus the movement's own.
  const accountOptions = accounts.filter((a) => a.isActive !== false || a.accountId === movement.accountId)

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    if (reason.trim() === '') return setError(t('editForm.errors.reason'))
    const request: EditTreasuryMovementRequest = { reason: reason.trim() }
    if (accountId !== movement.accountId) request.accountId = accountId
    if (!onlyAccount) {
      const value = parseAmount(amount)
      if (value === null) return setError(t('movementForm.errors.amount'))
      if (concept.trim() === '') return setError(t('movementForm.errors.concept'))
      if (date > isoDay(new Date())) return setError(t('movementForm.errors.future'))
      if (value !== movement.amount) request.amount = value
      if (date !== movement.businessDate.slice(0, 10)) request.date = date
      if (concept.trim() !== baseConcept(movement)) request.concept = concept.trim()
      if (reference.trim() !== (movement.documentReference ?? '')) request.reference = reference.trim()
    }
    if (Object.keys(request).length === 1) return setError(t('editForm.errors.nothing'))
    setError(null)
    setSaving(true)
    try {
      await editTreasuryMovement(movement.movementId, request)
      onSaved()
    } catch (err) {
      setError(errorText(err))
    } finally {
      setSaving(false)
    }
  }

  return (
    <FormShell title={t('editForm.title')}>
      <form onSubmit={(event) => void submit(event)} className="flex flex-col gap-4" noValidate>
        <p className="text-sm text-muted-foreground">
          {onlyAccount ? t('editForm.onlyAccountHint') : isTransfer ? t('editForm.transferHint') : t('editForm.hint')}
        </p>
        <div className="grid gap-4 sm:grid-cols-2">
          {!isTransfer && (
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="treasuryEditAccount">{t('movementForm.account')}</Label>
              <Select id="treasuryEditAccount" value={accountId} onChange={(e) => setAccountId(e.target.value)}>
                {accountOptions.map((account) => (
                  <option key={account.accountId} value={account.accountId}>
                    {accountLabel(account, wholeCompany)}
                  </option>
                ))}
              </Select>
            </div>
          )}
          {!onlyAccount && (
            <>
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="treasuryEditAmount">{t('movementForm.amount')}</Label>
                <Input id="treasuryEditAmount" inputMode="decimal" value={amount} onChange={(e) => setAmount(e.target.value)} />
              </div>
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="treasuryEditDate">{t('movementForm.date')}</Label>
                <Input id="treasuryEditDate" type="date" value={date} max={isoDay(new Date())} onChange={(e) => setDate(e.target.value)} />
              </div>
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="treasuryEditConcept">{t('movementForm.concept')}</Label>
                <Input id="treasuryEditConcept" value={concept} maxLength={200} onChange={(e) => setConcept(e.target.value)} />
              </div>
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="treasuryEditReference">{t('movementForm.reference')}</Label>
                <Input id="treasuryEditReference" value={reference} maxLength={60} onChange={(e) => setReference(e.target.value)} />
              </div>
            </>
          )}
          <div className="flex flex-col gap-1.5 sm:col-span-2">
            <Label htmlFor="treasuryEditReason">{t('editForm.reason')}</Label>
            <Input
              id="treasuryEditReason"
              value={reason}
              maxLength={200}
              placeholder={t('editForm.reasonPlaceholder')}
              onChange={(e) => setReason(e.target.value)}
            />
          </div>
        </div>
        <ErrorLine error={error} />
        <div className="flex gap-2">
          <Button type="submit" disabled={saving}>
            {saving ? t('saving') : t('editForm.save')}
          </Button>
          <Button type="button" variant="outline" onClick={onCancel}>
            {t('cancel')}
          </Button>
        </div>
      </form>
    </FormShell>
  )
}
