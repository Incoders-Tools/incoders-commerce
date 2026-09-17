import { useState, type FormEvent } from 'react'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import * as accountApi from '@/api/account'

/**
 * Anonymous reset confirm (commerce-password-recovery design.md "Reset
 * link, no router"). The token is supplied by `useResetToken()` (read once
 * from `?token=` at mount); on success this screen clears the token via
 * `clear()`, which scrubs the URL/history and drops back to sign-in — no
 * router dependency.
 */
export function ResetPasswordScreen({ token, onSuccess }: { token: string; onSuccess: () => void }) {
  const [newPassword, setNewPassword] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault()
    setSubmitting(true)
    setError(null)
    try {
      await accountApi.confirmPasswordReset({ token, newPassword })
      onSuccess()
    } catch {
      // Generic message: the endpoint returns the same 401 for an unknown,
      // expired, or already-used token, so this screen must not guess which.
      setError('This reset link is invalid or has expired.')
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <Card className="mx-auto mt-16 w-full max-w-sm">
      <CardHeader>
        <CardTitle>Reset your password</CardTitle>
      </CardHeader>
      <CardContent>
        <form className="flex flex-col gap-4" onSubmit={handleSubmit}>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="reset-new-password">New password</Label>
            <Input
              id="reset-new-password"
              type="password"
              value={newPassword}
              onChange={(e) => setNewPassword(e.target.value)}
              required
            />
          </div>
          {error && (
            <p role="alert" className="text-sm text-red-600">
              {error}
            </p>
          )}
          <Button type="submit" disabled={submitting}>
            {submitting ? 'Resetting…' : 'Reset password'}
          </Button>
        </form>
      </CardContent>
    </Card>
  )
}
