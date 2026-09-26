import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import * as accountApi from '@/api/account'

/**
 * Anonymous forgot-password start (commerce-password-recovery design.md
 * "Reset request (anonymous)"). The confirmation message is rendered
 * IDENTICALLY regardless of whether the submitted email matched a real
 * account or whether the request even reaches the server successfully —
 * `/account/reset-password/request` always answers 202, and this screen
 * never distinguishes success/failure/unknown-email from one another
 * (spec: "Unknown email looks identical to a known one").
 */
export function ForgotPasswordScreen({ onBackToSignIn }: { onBackToSignIn: () => void }) {
  const { t } = useTranslation('auth')
  const [email, setEmail] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [submitted, setSubmitted] = useState(false)

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault()
    setSubmitting(true)
    try {
      await accountApi.requestPasswordReset({ email })
    } catch {
      // Deliberately swallowed: the confirmation below is shown regardless
      // of outcome, so a mis-addressed or unreachable-API request never
      // reveals anything different from a known-email success.
    } finally {
      setSubmitting(false)
      setSubmitted(true)
    }
  }

  return (
    <Card className="mx-auto mt-16 w-full max-w-sm">
      <CardHeader>
        <CardTitle>{t('forgotPassword.title')}</CardTitle>
      </CardHeader>
      <CardContent>
        {submitted ? (
          <div className="flex flex-col gap-4">
            <p role="status" className="text-sm text-neutral-700">
              {t('forgotPassword.confirmation')}
            </p>
            <Button variant="outline" onClick={onBackToSignIn}>
              {t('forgotPassword.backToSignIn')}
            </Button>
          </div>
        ) : (
          <form className="flex flex-col gap-4" onSubmit={handleSubmit}>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="forgot-email">{t('forgotPassword.emailLabel')}</Label>
              <Input
                id="forgot-email"
                type="email"
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                placeholder="jane@example.com"
                required
              />
            </div>
            <Button type="submit" disabled={submitting}>
              {submitting ? t('forgotPassword.submitting') : t('forgotPassword.submit')}
            </Button>
            <Button type="button" variant="outline" onClick={onBackToSignIn}>
              {t('forgotPassword.backToSignIn')}
            </Button>
          </form>
        )}
      </CardContent>
    </Card>
  )
}
