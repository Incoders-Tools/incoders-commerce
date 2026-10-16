import { ArrowDown, ArrowUp, Plus, Trash2 } from 'lucide-react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { EmailField } from '@/components/form/EmailField'
import type { CustomerContact } from '@/api/types'

/** A contact row being edited: the server's fields plus a client-only `key` for stable React identity. */
export interface ContactDraft {
  key: string
  /** Present for a contact the server already knows (kept on replace-set). */
  id?: string
  firstName: string
  lastName: string
  phone: string
  email: string
  role: string
  isPrimary: boolean
}

let draftCounter = 0
export const newContactDraft = (overrides: Partial<ContactDraft> = {}): ContactDraft => ({
  key: `new-${++draftCounter}`,
  firstName: '',
  lastName: '',
  phone: '',
  email: '',
  role: '',
  isPrimary: false,
  ...overrides,
})

export const draftsFromContacts = (contacts: CustomerContact[]): ContactDraft[] =>
  [...contacts]
    .sort((a, b) => a.sortOrder - b.sortOrder)
    .map((contact) =>
      newContactDraft({
        key: contact.id,
        id: contact.id,
        firstName: contact.firstName,
        lastName: contact.lastName ?? '',
        phone: contact.phone ?? '',
        email: contact.email ?? '',
        role: contact.role ?? '',
        isPrimary: contact.isPrimary,
      }),
    )

interface ContactsEditorProps {
  contacts: ContactDraft[]
  onChange: (contacts: ContactDraft[]) => void
  /** Keys of rows whose first name is missing, flagged after a failed submit. */
  invalidKeys?: ReadonlySet<string>
  /** Keys of rows whose email a submit (or the server) refused. */
  invalidEmailKeys?: ReadonlySet<string>
}

/**
 * Presentational editor for a customer's contacts: add, remove, reorder
 * (up/down buttons, keyboard friendly) and a single "primary" radio. Each row
 * is a labelled group; it lays out as a card on phones and as one dense row
 * of fields from `lg:` up. The first contact added becomes primary.
 */
export function ContactsEditor({ contacts, onChange, invalidKeys, invalidEmailKeys }: ContactsEditorProps) {
  const { t } = useTranslation('customers')

  const update = (key: string, patch: Partial<ContactDraft>) =>
    onChange(contacts.map((contact) => (contact.key === key ? { ...contact, ...patch } : contact)))

  const setPrimary = (key: string) =>
    onChange(contacts.map((contact) => ({ ...contact, isPrimary: contact.key === key })))

  const move = (index: number, delta: -1 | 1) => {
    const target = index + delta
    if (target < 0 || target >= contacts.length) return
    const next = [...contacts]
    ;[next[index], next[target]] = [next[target], next[index]]
    onChange(next)
  }

  const add = () => onChange([...contacts, newContactDraft({ isPrimary: contacts.length === 0 })])

  return (
    <div className="flex flex-col gap-4">
      {contacts.length === 0 && <p className="text-sm text-muted-foreground">{t('form.contacts.empty')}</p>}
      {contacts.map((contact, index) => {
        const position = index + 1
        const invalid = invalidKeys?.has(contact.key) ?? false
        const fieldId = (name: string) => `contact-${contact.key}-${name}`
        return (
          <div
            key={contact.key}
            role="group"
            aria-label={t('form.contacts.rowLabel', { position })}
            className="grid grid-cols-1 gap-3 rounded-md border border-border bg-card p-3 sm:grid-cols-2 lg:grid-cols-6"
          >
            <div className="flex flex-col gap-1.5 lg:col-span-1">
              <Label htmlFor={fieldId('firstName')}>{t('form.contacts.firstName')}</Label>
              <Input
                id={fieldId('firstName')}
                value={contact.firstName}
                aria-required="true"
                aria-invalid={invalid ? true : undefined}
                aria-describedby={invalid ? fieldId('error') : undefined}
                onChange={(e) => update(contact.key, { firstName: e.target.value })}
              />
              {invalid && (
                <p id={fieldId('error')} className="text-xs text-destructive">
                  {t('form.contacts.firstNameRequired')}
                </p>
              )}
            </div>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor={fieldId('lastName')}>{t('form.contacts.lastName')}</Label>
              <Input
                id={fieldId('lastName')}
                value={contact.lastName}
                onChange={(e) => update(contact.key, { lastName: e.target.value })}
              />
            </div>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor={fieldId('phone')}>{t('form.contacts.phone')}</Label>
              <Input
                id={fieldId('phone')}
                type="tel"
                value={contact.phone}
                onChange={(e) => update(contact.key, { phone: e.target.value })}
              />
            </div>
            <EmailField
              id={fieldId('email')}
              label={t('form.contacts.email')}
              value={contact.email}
              onChange={(email) => update(contact.key, { email })}
              error={invalidEmailKeys?.has(contact.key) ? t('common:email.invalid') : null}
            />
            <div className="flex flex-col gap-1.5">
              <Label htmlFor={fieldId('role')}>{t('form.contacts.role')}</Label>
              <Input
                id={fieldId('role')}
                value={contact.role}
                onChange={(e) => update(contact.key, { role: e.target.value })}
              />
            </div>
            <div className="flex items-end justify-between gap-2 sm:col-span-2 lg:col-span-1">
              <div className="flex items-center gap-2 pb-2">
                <input
                  id={fieldId('primary')}
                  type="radio"
                  name="contact-primary"
                  checked={contact.isPrimary}
                  onChange={() => setPrimary(contact.key)}
                />
                <Label htmlFor={fieldId('primary')}>{t('form.contacts.primary')}</Label>
              </div>
              <div className="flex gap-1">
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  aria-label={t('form.contacts.moveUp', { position })}
                  disabled={index === 0}
                  onClick={() => move(index, -1)}
                >
                  <ArrowUp aria-hidden="true" className="size-4" />
                </Button>
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  aria-label={t('form.contacts.moveDown', { position })}
                  disabled={index === contacts.length - 1}
                  onClick={() => move(index, 1)}
                >
                  <ArrowDown aria-hidden="true" className="size-4" />
                </Button>
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  aria-label={t('form.contacts.remove', { position })}
                  onClick={() => onChange(contacts.filter((entry) => entry.key !== contact.key))}
                >
                  <Trash2 aria-hidden="true" className="size-4" />
                </Button>
              </div>
            </div>
          </div>
        )
      })}
      <div>
        <Button type="button" variant="outline" size="sm" onClick={add}>
          <Plus aria-hidden="true" className="size-4" />
          {t('form.contacts.add')}
        </Button>
      </div>
    </div>
  )
}
