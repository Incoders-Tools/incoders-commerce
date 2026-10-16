/**
 * The human form of a branch's short code: at least two digits (`01`, `02`,
 * ... `99`, `100`). Mirrors `Commerce.Domain.Tenancy.BranchCode.Format()` —
 * keep the two in step. The code is the `{branch}` part of document numbers
 * such as `V01-C2-125`.
 */
export function formatBranchCode(code: number): string {
  return String(code).padStart(2, '0')
}
