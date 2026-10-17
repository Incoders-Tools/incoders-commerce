import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import * as accountApi from '@/api/account'

/**
 * Anonymous reset confirm (commerce-web-routing design.md "`useResetToken`
 * retirement"). The token is supplied by the route layer (`ResetPasswordRoute`,
 * reading `useParams().token` from `/reset-password/:token`) — this screen's
 * `{ token, onSuccess }` props are unchanged and it stays router-free.
 */

/**
 * Shared with `ResetPasswordRoute`'s bare-`/reset-password` (no token)
 * treatment, so the two error states can't drift (design.md "`useResetToken`
 * retirement"). Kept as an i18n key rather than a resolved literal so both
 * places translate the same key instead of duplicating a string.
 */
export const INVALID_RESET_LINK_MESSAGE_KEY = 'resetPassword.invalidLink' as const

export function ResetPasswordScreen({ token, onSuccess }: { token: string; onSuccess: () => void }) {
  const { t } = useTranslation('auth')
  const [newPassword, setNewPassword] = useState('')
  const [confirmation, setConfirmation] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault()
    setError(null)
    // railway-deploy-pipeline S15/S16: nothing is sent while the two fields differ or the password is too short.
    if (newPassword !== confirmation) {
      setError(t('resetPassword.mismatch'))
      return
    }
    if (!accountApi.isPasswordLongEnough(newPassword)) {
      setError(t('common:password.tooShort', { count: accountApi.PASSWORD_MINIMUM_LENGTH }))
      return
    }
    setSubmitting(true)
    try {
      await accountApi.confirmPasswordReset({ token, newPassword })
      onSuccess()
    } catch (err) {
      // A too-short password is the server's 400 (checked before the token, which stays usable); any other failure
      // gets the generic message: the endpoint returns the same 401 for an unknown, expired, or already-used token,
      // so this screen must not guess which.
      setError(
        accountApi.isPasswordRejection(err, 'newPassword')
          ? t('common:password.tooShort', { count: accountApi.PASSWORD_MINIMUM_LENGTH })
          : t(INVALID_RESET_LINK_MESSAGE_KEY),
      )
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <Card className="mx-auto mt-16 w-full max-w-sm">
      <CardHeader>
        <CardTitle>{t('resetPassword.title')}</CardTitle>
      </CardHeader>
      <CardContent>
        <form className="flex flex-col gap-4" onSubmit={handleSubmit}>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="reset-new-password">{t('resetPassword.newPasswordLabel')}</Label>
            <Input
              id="reset-new-password"
              type="password"
              value={newPassword}
              onChange={(e) => setNewPassword(e.target.value)}
              aria-describedby="reset-new-password-hint"
              required
            />
            <p id="reset-new-password-hint" className="text-xs text-muted-foreground">
              {t('common:password.hint', { count: accountApi.PASSWORD_MINIMUM_LENGTH })}
            </p>
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="reset-confirm-password">{t('resetPassword.confirmPasswordLabel')}</Label>
            <Input
              id="reset-confirm-password"
              type="password"
              value={confirmation}
              onChange={(e) => setConfirmation(e.target.value)}
              required
            />
          </div>
          {error && (
            <p role="alert" className="text-sm text-red-600">
              {error}
            </p>
          )}
          <Button type="submit" disabled={submitting}>
            {submitting ? t('resetPassword.submitting') : t('resetPassword.submit')}
          </Button>
        </form>
      </CardContent>
    </Card>
  )
}
