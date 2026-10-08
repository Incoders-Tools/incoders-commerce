import { useCallback, useEffect, useMemo, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select } from '@/components/ui/select'
import { Textarea } from '@/components/ui/textarea'
import { DataView, type DataViewColumn } from '@/components/data/DataView'
import { PageHeader } from '@/components/data/PageHeader'
import { RowActions } from '@/components/data/RowActions'
import { FormPage } from '@/components/layout/FormPage'
import { ApiError } from '@/api/client'
import {
  PAY_FREQUENCIES,
  createEmployee,
  employeeRolesApi,
  giveAdvance,
  listEmployees,
  setEmployeeActive,
  updateEmployee,
  type EmployeeRecord,
  type PayFrequency,
} from '@/api/employees'
import { listTreasuryAccounts, type TreasuryAccount } from '@/api/treasury'
import type { MasterDataEntry, SelectableBranch } from '@/api/types'
import { useOptionalBranchContext } from '@/branch/BranchContext'
import { formatMoney } from '@/dashboard/format'
import { todayIso } from '@/lib/isoDate'
import { parseAmount } from '@/treasury/treasuryInput'
import { cn } from '@/lib/utils'

type Page = { kind: 'list' } | { kind: 'form'; employee: EmployeeRecord | null } | { kind: 'advance'; employee: EmployeeRecord }

/** The machine codes of a refused write, in plain words. */
function useErrorText() {
  const { t } = useTranslation('employees')
  return (err: unknown) =>
    err instanceof ApiError && err.code ? t(`errors.codes.${err.code}`, { defaultValue: err.message }) : t('errors.save')
}

/**
 * The staff (PRD 9.19), by branch: file number, name, position, pay, what the business owes each one (or each one owes)
 * and the goods taken to deduct. From here an employee is created, edited, given an advance, deactivated, and its
 * current account opened. The payroll lives in "Sueldos".
 */
export function EmployeesScreen() {
  const { t } = useTranslation('employees')
  const branchContext = useOptionalBranchContext()
  const branches: SelectableBranch[] = branchContext?.selectableBranches ?? []
  const [branchFilter, setBranchFilter] = useState(branchContext?.selectedBranch?.id ?? '')
  const [showInactive, setShowInactive] = useState(false)
  const [employees, setEmployees] = useState<EmployeeRecord[]>([])
  const [roles, setRoles] = useState<MasterDataEntry[]>([])
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [status, setStatus] = useState<string | null>(null)
  const [page, setPage] = useState<Page>({ kind: 'list' })
  const errorText = useErrorText()

  const load = useCallback(async () => {
    setLoading(true)
    setLoadError(null)
    try {
      setEmployees(await listEmployees({ branchId: branchFilter || undefined, includeInactive: showInactive }))
    } catch {
      setLoadError(t('errors.load'))
    } finally {
      setLoading(false)
    }
  }, [branchFilter, showInactive, t])

  useEffect(() => {
    void load()
  }, [load])

  useEffect(() => {
    employeeRolesApi.list(true).then(
      (list) => setRoles(Array.isArray(list) ? list : []),
      () => setRoles([]),
    )
  }, [])

  const toggleActive = async (employee: EmployeeRecord) => {
    try {
      await setEmployeeActive(employee.id, !employee.isActive)
      setStatus(employee.isActive ? t('status.deactivated', { name: employee.fullName }) : t('status.activated', { name: employee.fullName }))
      await load()
    } catch (err) {
      setStatus(errorText(err))
    }
  }

  const columns: DataViewColumn<EmployeeRecord>[] = [
    { key: 'file', header: t('columns.file'), cell: (e) => <span className="tabular-nums">{e.fileNumber}</span> },
    {
      key: 'name',
      header: t('columns.name'),
      cell: (e) => (
        <span className={cn('font-medium', !e.isActive && 'text-muted-foreground')}>
          {e.fullName}
          {!e.isActive && <span className="ml-2 rounded bg-muted px-1.5 py-0.5 text-xs">{t('inactive')}</span>}
        </span>
      ),
    },
    { key: 'role', header: t('columns.role'), cell: (e) => e.roleName ?? '—', hideOnMobile: true },
    { key: 'branch', header: t('columns.branch'), cell: (e) => e.branchName ?? '—', hideOnMobile: true },
    {
      key: 'pay',
      header: t('columns.pay'),
      cell: (e) => `${formatMoney(e.baseSalary)} ${t(`frequencyShort.${e.payFrequency}`)}`,
    },
    {
      key: 'balance',
      header: t('columns.balance'),
      headerHint: t('columns.balanceHint'),
      cell: (e) => (
        <span className={cn('tabular-nums', e.balance < 0 && 'text-destructive')}>
          {e.balance === 0 ? '—' : e.balance > 0 ? t('owedToThem', { amount: formatMoney(e.balance) }) : t('owesUs', { amount: formatMoney(-e.balance) })}
        </span>
      ),
    },
    {
      key: 'goods',
      header: t('columns.goods'),
      cell: (e) => (e.customerId === null ? t('noGoods') : e.purchasesOwed > 0 ? formatMoney(e.purchasesOwed) : '—'),
      hideOnMobile: true,
    },
  ]

  if (page.kind === 'form') {
    return (
      <EmployeeForm
        employee={page.employee}
        roles={roles}
        branches={branches}
        defaultBranchId={branchContext?.selectedBranch?.id ?? branches[0]?.id ?? ''}
        onCancel={() => setPage({ kind: 'list' })}
        onSaved={(saved) => {
          setPage({ kind: 'list' })
          setStatus(t('status.saved', { name: saved.fullName, file: saved.fileNumber }))
          void load()
        }}
      />
    )
  }

  if (page.kind === 'advance') {
    return (
      <AdvanceForm
        employee={page.employee}
        onCancel={() => setPage({ kind: 'list' })}
        onSaved={(amount) => {
          setPage({ kind: 'list' })
          setStatus(t('status.advanced', { name: page.employee.fullName, amount: formatMoney(amount) }))
          void load()
        }}
      />
    )
  }

  return (
    <section className="flex w-full flex-col gap-6">
      <PageHeader
        title={t('title')}
        description={t('description')}
        actions={
          <Button type="button" onClick={() => setPage({ kind: 'form', employee: null })}>
            {t('actions.create')}
          </Button>
        }
      />

      <div className="flex flex-wrap items-end gap-4">
        {branches.length > 1 && (
          <div className="flex w-56 flex-col gap-1.5">
            <Label htmlFor="employeesBranch">{t('filters.branch')}</Label>
            <Select id="employeesBranch" value={branchFilter} onChange={(e) => setBranchFilter(e.target.value)}>
              <option value="">{t('filters.allBranches')}</option>
              {branches.map((branch) => (
                <option key={branch.id} value={branch.id}>
                  {branch.name}
                </option>
              ))}
            </Select>
          </div>
        )}
        <label className="flex items-center gap-2 text-sm text-muted-foreground">
          <input type="checkbox" checked={showInactive} onChange={(e) => setShowInactive(e.target.checked)} />
          {t('filters.showInactive')}
        </label>
      </div>

      {status && (
        <p role="status" className="text-sm text-muted-foreground">
          {status}
        </p>
      )}

      <DataView
        items={employees}
        columns={columns}
        getRowKey={(e) => e.id}
        view="table"
        loading={loading}
        loadErrorMessage={loadError}
        emptyMessage={t('empty')}
        renderActions={(employee) => (
          <RowActions
            label={employee.fullName}
            actions={[
              { key: 'edit', label: t('actions.edit'), onSelect: () => setPage({ kind: 'form', employee }) },
              { key: 'account', label: t('actions.account'), to: `/app/employees/${employee.id}/account` },
              { key: 'advance', label: t('actions.advance'), onSelect: () => setPage({ kind: 'advance', employee }), hidden: !employee.isActive },
              {
                key: 'toggle',
                label: employee.isActive ? t('actions.deactivate') : t('actions.activate'),
                onSelect: () => void toggleActive(employee),
                destructive: employee.isActive,
              },
            ]}
          />
        )}
      />
    </section>
  )
}

function EmployeeForm({
  employee,
  roles,
  branches,
  defaultBranchId,
  onCancel,
  onSaved,
}: {
  employee: EmployeeRecord | null
  roles: readonly MasterDataEntry[]
  branches: readonly SelectableBranch[]
  defaultBranchId: string
  onCancel: () => void
  onSaved: (employee: EmployeeRecord) => void
}) {
  const { t } = useTranslation('employees')
  const errorText = useErrorText()
  const [firstName, setFirstName] = useState(employee?.firstName ?? '')
  const [lastName, setLastName] = useState(employee?.lastName ?? '')
  const [fileNumber, setFileNumber] = useState(employee ? String(employee.fileNumber) : '')
  const [branchId, setBranchId] = useState(employee?.branchId ?? defaultBranchId)
  const [roleId, setRoleId] = useState(employee?.roleId ?? '')
  const [documentNumber, setDocumentNumber] = useState(employee?.documentNumber ?? '')
  const [cuil, setCuil] = useState(employee?.cuil ?? '')
  const [phone, setPhone] = useState(employee?.phone ?? '')
  const [email, setEmail] = useState(employee?.email ?? '')
  const [address, setAddress] = useState(employee?.address ?? '')
  const [hireDate, setHireDate] = useState(employee?.hireDate ?? '')
  const [payFrequency, setPayFrequency] = useState<PayFrequency>(employee?.payFrequency ?? 'Monthly')
  const [baseSalary, setBaseSalary] = useState(employee ? String(employee.baseSalary).replace('.', ',') : '')
  const [takesGoods, setTakesGoods] = useState(employee ? employee.customerId !== null : true)
  const [notes, setNotes] = useState(employee?.notes ?? '')
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)

  const roleOptions = useMemo(() => roles.filter((role) => role.isActive || role.id === employee?.roleId), [roles, employee])

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    if (firstName.trim() === '' || lastName.trim() === '') return setError(t('form.errors.name'))
    if (branchId === '') return setError(t('form.errors.branch'))
    const salary = baseSalary.trim() === '' ? 0 : parseAmount(baseSalary)
    if (salary === null) return setError(t('form.errors.salary'))
    const file = fileNumber.trim() === '' ? undefined : Number(fileNumber)
    if (file !== undefined && (!Number.isInteger(file) || file <= 0)) return setError(t('form.errors.file'))
    setError(null)
    setSaving(true)
    try {
      const request = {
        branchId,
        ...(file !== undefined ? { fileNumber: file } : {}),
        firstName: firstName.trim(),
        lastName: lastName.trim(),
        documentNumber: documentNumber.trim() || null,
        cuil: cuil.trim() || null,
        roleId: roleId || null,
        phone: phone.trim() || null,
        email: email.trim() || null,
        address: address.trim() || null,
        hireDate: hireDate || null,
        payFrequency,
        baseSalary: salary,
        notes: notes.trim() || null,
        takesGoods,
      }
      onSaved(employee ? await updateEmployee(employee.id, request) : await createEmployee(request))
    } catch (err) {
      setError(errorText(err))
    } finally {
      setSaving(false)
    }
  }

  return (
    <FormPage
      title={employee ? t('form.editTitle', { name: employee.fullName }) : t('form.createTitle')}
      description={t('form.description')}
      onBack={onCancel}
      backLabel={t('form.back')}
    >
      <form className="flex max-w-3xl flex-col gap-6" onSubmit={(event) => void submit(event)} noValidate>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field id="employeeLastName" label={t('form.lastName')} value={lastName} onChange={setLastName} autoFocus />
          <Field id="employeeFirstName" label={t('form.firstName')} value={firstName} onChange={setFirstName} />
          <Field
            id="employeeFileNumber"
            label={t('form.fileNumber')}
            value={fileNumber}
            onChange={setFileNumber}
            inputMode="numeric"
            placeholder={employee ? undefined : t('form.fileNumberPlaceholder')}
          />
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="employeeBranch">{t('form.branch')}</Label>
            <Select id="employeeBranch" value={branchId} onChange={(e) => setBranchId(e.target.value)}>
              {branchId === '' && <option value="">{t('form.chooseBranch')}</option>}
              {branches.map((branch) => (
                <option key={branch.id} value={branch.id}>
                  {branch.name}
                </option>
              ))}
            </Select>
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="employeeRole">{t('form.role')}</Label>
            <Select id="employeeRole" value={roleId} onChange={(e) => setRoleId(e.target.value)}>
              <option value="">{t('form.noRole')}</option>
              {roleOptions.map((role) => (
                <option key={role.id} value={role.id}>
                  {role.name}
                </option>
              ))}
            </Select>
            <p className="text-xs text-muted-foreground">{t('form.roleHint')}</p>
          </div>
          <Field id="employeeHireDate" label={t('form.hireDate')} value={hireDate} onChange={setHireDate} type="date" />
          <Field id="employeeDocument" label={t('form.document')} value={documentNumber} onChange={setDocumentNumber} inputMode="numeric" />
          <Field id="employeeCuil" label={t('form.cuil')} value={cuil} onChange={setCuil} inputMode="numeric" placeholder="20-30111222-3" />
          <Field id="employeePhone" label={t('form.phone')} value={phone} onChange={setPhone} />
          <Field id="employeeEmail" label={t('form.email')} value={email} onChange={setEmail} type="email" />
          <div className="sm:col-span-2">
            <Field id="employeeAddress" label={t('form.address')} value={address} onChange={setAddress} />
          </div>
        </div>

        <fieldset className="grid gap-4 rounded-md border border-border p-4 sm:grid-cols-2">
          <legend className="px-1 text-sm font-medium">{t('form.payTitle')}</legend>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="employeeFrequency">{t('form.frequency')}</Label>
            <Select id="employeeFrequency" value={payFrequency} onChange={(e) => setPayFrequency(e.target.value as PayFrequency)}>
              {PAY_FREQUENCIES.map((frequency) => (
                <option key={frequency} value={frequency}>
                  {t(`frequency.${frequency}`)}
                </option>
              ))}
            </Select>
          </div>
          <Field
            id="employeeSalary"
            label={t('form.salary')}
            value={baseSalary}
            onChange={setBaseSalary}
            inputMode="decimal"
            placeholder={t('form.salaryPlaceholder')}
          />
          <p className="text-xs text-muted-foreground sm:col-span-2">{t('form.payHint')}</p>
          <label className="flex items-start gap-2 text-sm sm:col-span-2">
            <input type="checkbox" className="mt-0.5" checked={takesGoods} disabled={employee?.customerId != null} onChange={(e) => setTakesGoods(e.target.checked)} />
            <span>
              {t('form.takesGoods')}
              <span className="block text-xs text-muted-foreground">{t('form.takesGoodsHint')}</span>
            </span>
          </label>
        </fieldset>

        <div className="flex flex-col gap-1.5">
          <Label htmlFor="employeeNotes">{t('form.notes')}</Label>
          <Textarea id="employeeNotes" rows={2} maxLength={1000} value={notes} onChange={(e) => setNotes(e.target.value)} />
        </div>

        {error && (
          <p role="alert" className="text-sm text-destructive">
            {error}
          </p>
        )}
        <div className="flex gap-2">
          <Button type="submit" disabled={saving}>
            {saving ? t('form.saving') : t('form.save')}
          </Button>
          <Button type="button" variant="outline" onClick={onCancel}>
            {t('form.cancel')}
          </Button>
        </div>
      </form>
    </FormPage>
  )
}

/** An advance handed out: money out of a treasury account, a debit on the employee's account; the next payroll deducts it. */
function AdvanceForm({ employee, onCancel, onSaved }: { employee: EmployeeRecord; onCancel: () => void; onSaved: (amount: number) => void }) {
  const { t } = useTranslation('employees')
  const errorText = useErrorText()
  const [accounts, setAccounts] = useState<TreasuryAccount[]>([])
  const [accountId, setAccountId] = useState('')
  const [amount, setAmount] = useState('')
  const [date, setDate] = useState(todayIso())
  const [concept, setConcept] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)

  useEffect(() => {
    listTreasuryAccounts().then(
      (list) => {
        const active = list.filter((account) => account.isActive !== false)
        setAccounts(active)
        // The drawer of the employee's branch first: the usual place an advance comes from.
        setAccountId(active.find((a) => a.branchId === employee.branchId && a.kind === 'Cash')?.accountId ?? active[0]?.accountId ?? '')
      },
      () => setAccounts([]),
    )
  }, [employee.branchId])

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    const value = parseAmount(amount)
    if (value === null) return setError(t('advance.errors.amount'))
    if (accountId === '') return setError(t('advance.errors.account'))
    setError(null)
    setSaving(true)
    try {
      await giveAdvance(employee.id, { amount: value, accountId, date, ...(concept.trim() ? { concept: concept.trim() } : {}) })
      onSaved(value)
    } catch (err) {
      setError(errorText(err))
    } finally {
      setSaving(false)
    }
  }

  return (
    <FormPage title={t('advance.title', { name: employee.fullName })} description={t('advance.description')} onBack={onCancel} backLabel={t('form.back')}>
      <form className="flex max-w-xl flex-col gap-4" onSubmit={(event) => void submit(event)} noValidate>
        <Field id="advanceAmount" label={t('advance.amount')} value={amount} onChange={setAmount} inputMode="decimal" placeholder="50000" autoFocus />
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="advanceAccount">{t('advance.account')}</Label>
          <Select id="advanceAccount" value={accountId} onChange={(e) => setAccountId(e.target.value)}>
            {accounts.map((account) => (
              <option key={account.accountId} value={account.accountId}>
                {account.name}
              </option>
            ))}
          </Select>
        </div>
        <Field id="advanceDate" label={t('advance.date')} value={date} onChange={setDate} type="date" />
        <Field id="advanceConcept" label={t('advance.concept')} value={concept} onChange={setConcept} placeholder={t('advance.conceptPlaceholder')} />
        {error && (
          <p role="alert" className="text-sm text-destructive">
            {error}
          </p>
        )}
        <div className="flex gap-2">
          <Button type="submit" disabled={saving}>
            {saving ? t('form.saving') : t('advance.save')}
          </Button>
          <Button type="button" variant="outline" onClick={onCancel}>
            {t('form.cancel')}
          </Button>
        </div>
      </form>
    </FormPage>
  )
}

function Field({
  id,
  label,
  value,
  onChange,
  type = 'text',
  inputMode,
  placeholder,
  autoFocus,
}: {
  id: string
  label: string
  value: string
  onChange: (value: string) => void
  type?: string
  inputMode?: 'numeric' | 'decimal'
  placeholder?: string
  autoFocus?: boolean
}) {
  return (
    <div className="flex flex-col gap-1.5">
      <Label htmlFor={id}>{label}</Label>
      <Input
        id={id}
        type={type}
        inputMode={inputMode}
        value={value}
        placeholder={placeholder}
        autoFocus={autoFocus}
        onChange={(e) => onChange(e.target.value)}
      />
    </div>
  )
}
