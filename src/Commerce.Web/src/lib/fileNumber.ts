/**
 * employee-lifecycle E1: the legajo as the web and the POS show it (`EmployeeFileNumber.Format`): zero-padded to four
 * digits ("0007"), a longer one whole.
 */
export function formatFileNumber(fileNumber: number): string {
  return String(fileNumber).padStart(4, '0')
}
