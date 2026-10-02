import { useEffect, useId, useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'

interface ConfirmDialogProps {
  title: string
  message: ReactNode
  confirmLabel: string
  busyLabel: string
  busy?: boolean
  destructive?: boolean
  onConfirm: (reason: string) => void
  onCancel: () => void
  /** When present the operator must write a reason (trimmed, up to `maxLength`) before confirming. */
  reason?: { label: string; requiredMessage: string; maxLength?: number }
}

/**
 * Modal confirmation for consequential actions (confirming a reception moves
 * stock and posts an invoice; voiding reverses both). Presentational: the
 * caller owns what happens on confirm. Escape cancels while idle.
 */
export function ConfirmDialog({
  title,
  message,
  confirmLabel,
  busyLabel,
  busy = false,
  destructive = false,
  onConfirm,
  onCancel,
  reason,
}: ConfirmDialogProps) {
  const { t } = useTranslation('common')
  const titleId = useId()
  const messageId = useId()
  const reasonId = useId()
  const [reasonText, setReasonText] = useState('')
  const [reasonError, setReasonError] = useState(false)

  useEffect(() => {
    const cancelOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !busy) onCancel()
    }
    document.addEventListener('keydown', cancelOnEscape)
    return () => document.removeEventListener('keydown', cancelOnEscape)
  }, [busy, onCancel])

  const submit = () => {
    const trimmed = reasonText.trim()
    if (reason && trimmed === '') {
      setReasonError(true)
      return
    }
    onConfirm(trimmed)
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4">
      <div
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        aria-describedby={messageId}
        className="flex w-full max-w-md flex-col gap-4 rounded-lg border border-border bg-card p-5 shadow-lg"
      >
        <h2 id={titleId} className="text-base font-semibold text-foreground">
          {title}
        </h2>
        <p id={messageId} className="text-sm text-muted-foreground">
          {message}
        </p>
        {reason && (
          <div className="flex flex-col gap-1.5">
            <Label htmlFor={reasonId}>{reason.label}</Label>
            <Textarea
              id={reasonId}
              rows={3}
              maxLength={reason.maxLength}
              value={reasonText}
              aria-invalid={reasonError ? true : undefined}
              onChange={(event) => {
                setReasonText(event.target.value)
                setReasonError(false)
              }}
            />
            {reasonError && <p className="text-xs text-destructive">{reason.requiredMessage}</p>}
          </div>
        )}
        <div className="flex flex-wrap justify-end gap-2">
          <Button type="button" variant="outline" onClick={onCancel} disabled={busy}>
            {t('actions.cancel')}
          </Button>
          <Button type="button" variant={destructive ? 'destructive' : 'default'} onClick={submit} disabled={busy}>
            {busy ? busyLabel : confirmLabel}
          </Button>
        </div>
      </div>
    </div>
  )
}
