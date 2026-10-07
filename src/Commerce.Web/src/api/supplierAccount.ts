import * as account from './currentAccount'
import type { AccountMovement, AccountStatement, AccountSummary, RegisterMovementRequest, ReverseMovementRequest } from './types'

// The supplier side of `./currentAccount`, kept for its existing callers.

export function registerMovement(supplierId: string, request: RegisterMovementRequest): Promise<AccountMovement> {
  return account.registerMovement('supplier', supplierId, request)
}

export function reverseMovement(
  supplierId: string,
  movementId: string,
  request: ReverseMovementRequest = {},
): Promise<AccountMovement> {
  return account.reverseMovement('supplier', supplierId, movementId, request)
}

export function getStatement(supplierId: string, range: { from?: string; to?: string } = {}): Promise<AccountStatement> {
  return account.getStatement('supplier', supplierId, range)
}

export function getSummary(supplierId: string, asOf?: string): Promise<AccountSummary> {
  return account.getSummary('supplier', supplierId, asOf)
}
