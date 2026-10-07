import { apiFetch } from './client'
import { createMasterDataApi } from './masterData'

// Endpoints/Employees.cs + Persistence/PostgresEmployeeStore.cs / PostgresPayrollStore.cs, mirrored. The staff of each
// branch and their INTERNAL payroll (PRD 9.19: no legal contributions). Administration only.

export type PayFrequency = 'Monthly' | 'Biweekly' | 'Weekly'
export const PAY_FREQUENCIES: PayFrequency[] = ['Monthly', 'Biweekly', 'Weekly']

export interface EmployeeRecord {
  id: string
  branchId: string
  branchName: string | null
  fileNumber: number
  firstName: string
  lastName: string
  fullName: string
  documentNumber: string | null
  cuil: string | null
  roleId: string | null
  roleName: string | null
  phone: string | null
  email: string | null
  address: string | null
  hireDate: string | null
  terminationDate: string | null
  payFrequency: PayFrequency
  baseSalary: number
  /** The linked customer the POS sells goods to on account; null while the employee takes no goods. */
  customerId: string | null
  notes: string | null
  isActive: boolean
  createdAtUtc: string
  updatedAtUtc: string
  /** What the business owes the employee on its account (negative: what the employee owes). */
  balance: number
  /** What its linked customer owes for goods: the next payroll deducts it. */
  purchasesOwed: number
}

export interface EmployeeRequest {
  branchId: string
  /** Omitted on create: the next file number of the organization. */
  fileNumber?: number
  firstName: string
  lastName: string
  documentNumber?: string | null
  cuil?: string | null
  roleId?: string | null
  phone?: string | null
  email?: string | null
  address?: string | null
  hireDate?: string | null
  payFrequency: PayFrequency
  baseSalary: number
  notes?: string | null
  /** Link a customer so the POS can sell goods to the employee on account. */
  takesGoods?: boolean
}

/** The positions catalog ("puestos"). */
export const employeeRolesApi = createMasterDataApi('/employees/roles')

export function listEmployees(filters: { branchId?: string; includeInactive?: boolean } = {}): Promise<EmployeeRecord[]> {
  const query = new URLSearchParams()
  if (filters.branchId) query.set('branchId', filters.branchId)
  if (filters.includeInactive) query.set('includeInactive', 'true')
  const queryString = query.toString()
  return apiFetch<EmployeeRecord[]>(queryString ? `/employees?${queryString}` : '/employees')
}

export function getEmployee(id: string): Promise<EmployeeRecord> {
  return apiFetch<EmployeeRecord>(`/employees/${id}`)
}

export function createEmployee(request: EmployeeRequest): Promise<EmployeeRecord> {
  return apiFetch<EmployeeRecord>('/employees', { method: 'POST', body: JSON.stringify(request) })
}

export function updateEmployee(id: string, request: EmployeeRequest): Promise<EmployeeRecord> {
  return apiFetch<EmployeeRecord>(`/employees/${id}`, { method: 'PUT', body: JSON.stringify(request) })
}

export function setEmployeeActive(id: string, isActive: boolean, terminationDate?: string): Promise<void> {
  return apiFetch<void>(`/employees/${id}/active`, {
    method: 'POST',
    body: JSON.stringify({ isActive, ...(terminationDate ? { terminationDate } : {}) }),
  })
}

/** An advance handed out from a treasury account: money out, a debit on the employee's account. */
export function giveAdvance(
  id: string,
  request: { amount: number; accountId: string; date?: string; concept?: string },
): Promise<{ advanceId: string }> {
  return apiFetch<{ advanceId: string }>(`/employees/${id}/advances`, { method: 'POST', body: JSON.stringify(request) })
}

// ---- payroll -----------------------------------------------------------------------------------

export type PayrollStatus = 'Draft' | 'Paid'
export type PayslipLineKind = 'Earning' | 'Deduction'
export type PayslipLineSource = 'BaseSalary' | 'Advances' | 'Manual'

export interface PayrollRunSummary {
  runId: string
  runNumber: number
  branchId: string
  periodFrom: string
  periodTo: string
  payFrequency: PayFrequency | null
  status: PayrollStatus
  notes: string | null
  employeeCount: number
  totalNet: number
  paidOn: string | null
  paymentAccountId: string | null
  createdAtUtc: string
}

export interface PayslipLine {
  lineNo: number
  kind: PayslipLineKind
  source: PayslipLineSource
  concept: string
  amount: number
}

export interface Payslip {
  payslipId: string
  employeeId: string
  fileNumber: number
  employeeName: string
  roleName: string | null
  cuil: string | null
  takesGoods: boolean
  lines: PayslipLine[]
  purchasesAmount: number
  purchasesDiscountPercent: number
  purchasesDiscount: number
  purchasesDeducted: number
  earnings: number
  deductions: number
  net: number
  netPaid: number
}

export interface PayrollRunDetail {
  run: PayrollRunSummary
  branchName: string | null
  paymentAccountName: string | null
  payslips: Payslip[]
}

export function listPayrollRuns(): Promise<PayrollRunSummary[]> {
  return apiFetch<PayrollRunSummary[]>('/payroll/runs')
}

export function getPayrollRun(id: string): Promise<PayrollRunDetail> {
  return apiFetch<PayrollRunDetail>(`/payroll/runs/${id}`)
}

export function createPayrollRun(request: {
  periodFrom: string
  periodTo: string
  payFrequency?: PayFrequency
  notes?: string
}): Promise<{ runId: string }> {
  return apiFetch<{ runId: string }>('/payroll/runs', { method: 'POST', body: JSON.stringify(request) })
}

export function updatePayslip(
  runId: string,
  payslipId: string,
  request: { lines: Omit<PayslipLine, 'lineNo'>[]; purchasesDiscountPercent: number },
): Promise<void> {
  return apiFetch<void>(`/payroll/runs/${runId}/payslips/${payslipId}`, { method: 'PUT', body: JSON.stringify(request) })
}

export function removePayslip(runId: string, payslipId: string): Promise<void> {
  return apiFetch<void>(`/payroll/runs/${runId}/payslips/${payslipId}`, { method: 'DELETE' })
}

export function deletePayrollRun(runId: string): Promise<void> {
  return apiFetch<void>(`/payroll/runs/${runId}`, { method: 'DELETE' })
}

export function payPayrollRun(runId: string, request: { accountId: string; paidOn?: string }): Promise<void> {
  return apiFetch<void>(`/payroll/runs/${runId}/pay`, { method: 'POST', body: JSON.stringify(request) })
}
