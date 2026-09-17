import { useEffect } from 'react'
import { Link, useLocation, useNavigate, type Location } from 'react-router'
import { buttonVariants } from '@/components/ui/button'
import { SignInScreen } from '@/screens/SignInScreen'
import { useAuth } from '@/auth/AuthContext'
import { resolveLandingPath } from './landing'

/**
 * Single shared `/login` route (web-app-routing spec: "Single Shared
 * `/login` Route"). `SignInScreen` keeps its existing props/behavior — it
 * calls `useAuth().signIn()` internally — so this route observes `user`
 * becoming non-null to know sign-in succeeded, then navigates to the
 * originally-requested deep link (`location.state.from`) or, absent one,
 * to `resolveLandingPath(user)` (design.md "Guard implementation").
 */
export function LoginRoute() {
  const { user } = useAuth()
  const navigate = useNavigate()
  const location = useLocation()
  const from = (location.state as { from?: Location } | null)?.from

  useEffect(() => {
    if (user) {
      navigate(from?.pathname ?? resolveLandingPath(user), { replace: true })
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [user])

  return (
    <div className="flex flex-col gap-3">
      <SignInScreen />
      <Link to="/forgot-password" className={buttonVariants({ variant: 'outline', size: 'sm', className: 'mx-auto' })}>
        Forgot password?
      </Link>
    </div>
  )
}
