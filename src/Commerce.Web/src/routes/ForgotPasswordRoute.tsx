import { useNavigate } from 'react-router'
import { ForgotPasswordScreen } from '@/screens/ForgotPasswordScreen'

/**
 * Thin route wrapper — `ForgotPasswordScreen`'s `{ onBackToSignIn }` prop is
 * unchanged, so its existing test needs no edit (design.md "container/
 * presentational" rule).
 */
export function ForgotPasswordRoute() {
  const navigate = useNavigate()
  return <ForgotPasswordScreen onBackToSignIn={() => navigate('/login')} />
}
