import { useState, type FormEvent } from 'react'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import * as accountApi from '@/api/account'

/**
 * Authenticated self-service password change (commerce-password-recovery
 * design.md "Renew (authenticated)"). Reachable from `AuthenticatedApp`'s
 * header via a `renew` tab (App.tsx).
 */
export function RenewPasswordScreen() {
  const [currentPassword, setCurrentPassword] = useState('')
  const [newPassword, setNewPassword] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [success, setSuccess] = useState(false)

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault()
    setSubmitting(true)
    setError(null)
    setSuccess(false)
    try {
      await accountApi.renewPassword({ currentPassword, newPassword })
      setSuccess(true)
      setCurrentPassword('')
      setNewPassword('')
    } catch {
      setError('Current password is incorrect.')
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <Card className="mx-auto mt-6 w-full max-w-sm">
      <CardHeader>
        <CardTitle>Change your password</CardTitle>
      </CardHeader>
      <CardContent>
        <form className="flex flex-col gap-4" onSubmit={handleSubmit}>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="renew-current-password">Current password</Label>
            <Input
              id="renew-current-password"
              type="password"
              value={currentPassword}
              onChange={(e) => setCurrentPassword(e.target.value)}
              required
            />
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="renew-new-password">New password</Label>
            <Input
              id="renew-new-password"
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
          {success && (
            <p role="status" className="text-sm text-green-700">
              Your password has been changed.
            </p>
          )}
          <Button type="submit" disabled={submitting}>
            {submitting ? 'Changing…' : 'Change password'}
          </Button>
        </form>
      </CardContent>
    </Card>
  )
}
