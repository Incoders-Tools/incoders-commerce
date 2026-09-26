import { useNavigate, useParams } from 'react-router'
import { useTranslation } from 'react-i18next'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { INVALID_RESET_LINK_MESSAGE_KEY, ResetPasswordScreen } from '@/screens/ResetPasswordScreen'

/**
 * `/reset-password/:token` (web-app-routing spec: "Reset-Password Uses a
 * Path Param, Not a Query String"). Reads the token via `useParams` — no
 * query string is ever read. A bare `/reset-password` (an old `?token=`
 * link, no path param) renders the invalid-link card using
 * `INVALID_RESET_LINK_MESSAGE`, shared with `ResetPasswordScreen`'s own
 * error branch so the two treatments cannot drift.
 */
export function ResetPasswordRoute() {
  const { t } = useTranslation('auth')
  const { token } = useParams()
  const navigate = useNavigate()

  if (!token) {
    return (
      <Card className="mx-auto mt-16 w-full max-w-sm">
        <CardHeader>
          <CardTitle>{t('resetPassword.title')}</CardTitle>
        </CardHeader>
        <CardContent>
          <p role="alert" className="text-sm text-red-600">
            {t(INVALID_RESET_LINK_MESSAGE_KEY)}
          </p>
        </CardContent>
      </Card>
    )
  }

  return <ResetPasswordScreen token={token} onSuccess={() => navigate('/login', { replace: true })} />
}
