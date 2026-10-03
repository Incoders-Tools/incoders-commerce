import { emailStatus } from '@/lib/email'
import type { ContactDraft } from './ContactsEditor'

/**
 * Which contact rows get their email flagged in `ContactsEditor`: shared by the customer and supplier forms, which
 * keep the flagged keys between a refused submit (or a 400) and the next edit.
 */

/** Keys of the rows whose email the shared rule refuses (a blank email is fine). */
export const invalidEmailKeys = (rows: ContactDraft[]): Set<string> =>
  new Set(rows.filter((row) => emailStatus(row.email) === 'invalid').map((row) => row.key))

/**
 * Keys of the rows a 400 `errors.contacts` refuses for their email ("contacts[1].email must be ..."), `sent` being
 * the rows in the order they were sent.
 */
export const refusedEmailKeys = (sent: ContactDraft[], messages: string[] | undefined): Set<string> =>
  new Set(
    (messages ?? []).flatMap((message) => {
      const index = /^contacts\[(\d+)\]\.email\b/.exec(message)?.[1]
      const row = index === undefined ? undefined : sent[Number(index)]
      return row ? [row.key] : []
    }),
  )

/** The flagged keys whose email is still the one that was flagged: editing an email clears its flag. */
export const keepUnchangedEmailFlags = (
  flagged: ReadonlySet<string>,
  before: ContactDraft[],
  after: ContactDraft[],
): ReadonlySet<string> => {
  if (flagged.size === 0) return flagged
  const emailOf = (rows: ContactDraft[], key: string) => rows.find((row) => row.key === key)?.email
  return new Set([...flagged].filter((key) => emailOf(after, key) === emailOf(before, key)))
}
