import i18next from '@/i18n'
import { getSelectedBranchId } from '@/branch/BranchContext'
import { getSelectedOrganizationId } from '@/organization/OrganizationContext'

// Not a React component: this module runs plain i18next (the singleton
// `initReactI18next` initializes in `main.tsx`/`test/setup.ts`) rather than
// the `useTranslation` hook, since `apiFetch` executes outside render.
function t(key: 'apiUnreachable' | 'requestFailedWithStatus', options?: Record<string, unknown>): string {
  return i18next.t(`errors:${key}`, options)
}

/**
 * Thin same-origin fetch wrapper. `credentials: 'include'` sends the
 * HttpOnly/Secure/SameSite=Lax Identity cookie Cloud.Api sets at sign-in
 * (design.md "Browser auth") — the SPA never reads or stores the cookie
 * itself, and never touches localStorage/sessionStorage for auth.
 */
export class ApiError extends Error {
  readonly status: number

  constructor(message: string, status: number) {
    super(message)
    this.name = 'ApiError'
    this.status = status
  }
}

/**
 * platform-administration spec, "Sysadmin Acts On A Selected Organization",
 * and admin-console spec, "Top Navbar Branch Switcher": attaches
 * `X-Organization-Id` when a system administrator has selected a target
 * organization (`organization/OrganizationContext.tsx`) and `X-Branch-Id`
 * when a branch is selected (`branch/BranchContext.tsx`), alongside each
 * other. The server honors either header only for a caller entitled to use
 * it and ignores it otherwise — this client never decides that, it only
 * reflects the current UI selection.
 */
function tenantHeaders(): HeadersInit {
  const organizationId = getSelectedOrganizationId()
  const branchId = getSelectedBranchId()
  return {
    ...(organizationId ? { 'X-Organization-Id': organizationId } : {}),
    ...(branchId ? { 'X-Branch-Id': branchId } : {}),
  }
}

export async function apiFetch<TResponse>(
  path: string,
  init?: RequestInit,
): Promise<TResponse> {
  let response: Response
  try {
    response = await fetch(path, {
      ...init,
      credentials: 'include',
      headers: {
        'Content-Type': 'application/json',
        ...tenantHeaders(),
        ...init?.headers,
      },
    })
  } catch {
    // Network-level failure: API unreachable (spec.md "API unreachable"
    // scenario) — surfaced as a typed error the caller renders visibly.
    throw new ApiError(t('apiUnreachable'), 0)
  }

  if (!response.ok) {
    let detail = response.statusText
    try {
      const body = await response.json()
      detail = body?.title ?? body?.detail ?? JSON.stringify(body)
    } catch {
      // Non-JSON error body; fall back to statusText.
    }
    throw new ApiError(detail || t('requestFailedWithStatus', { status: response.status }), response.status)
  }

  if (response.status === 204) {
    return undefined as TResponse
  }

  return (await response.json()) as TResponse
}

/**
 * Multipart variant for the Excel import upload (`POST /pricing/imports`):
 * deliberately does NOT set `Content-Type` — the browser must set it itself
 * (including the multipart boundary) when the body is a `FormData`. Reuses
 * `apiFetch`'s error-mapping shape otherwise.
 */
export async function apiFetchForm<TResponse>(path: string, formData: FormData): Promise<TResponse> {
  let response: Response
  try {
    response = await fetch(path, {
      method: 'POST',
      credentials: 'include',
      body: formData,
    })
  } catch {
    throw new ApiError(t('apiUnreachable'), 0)
  }

  if (!response.ok) {
    let detail = response.statusText
    try {
      const body = await response.json()
      detail = body?.title ?? body?.detail ?? JSON.stringify(body)
    } catch {
      // Non-JSON error body; fall back to statusText.
    }
    throw new ApiError(detail || t('requestFailedWithStatus', { status: response.status }), response.status)
  }

  return (await response.json()) as TResponse
}

/**
 * Variant for endpoints whose real, deliberate "denied" business outcome is
 * a NON-2xx response that still carries the full outcome DTO as its JSON
 * body — `Endpoints/Catalog.cs`'s rename and `Endpoints/Ordering.cs`'s order
 * submission both return `Results.Json(outcome, statusCode: 403)` for a
 * denial and `Results.Ok(outcome)` for success, so 403 there is a genuine
 * business answer, not a transport failure. Plain `apiFetch` cannot be used
 * for these: it throws on any non-2xx and discards that body — a real
 * browser E2E test (src/Commerce.Web/e2e/catalog.spec.ts) caught this
 * rendering every denial as a generic "Commerce.Cloud.Api" JSON-dump error
 * instead of the screen's own denial message, because the existing mocked
 * Vitest specs only ever mocked the 200 success path.
 *
 * A 403 with NO parseable JSON body (e.g. those same endpoints'
 * `Results.Forbid()` for a missing/revoked actor — not a business outcome at
 * all) still falls through to the generic error path below.
 */
export async function apiFetchOutcome<TOutcome>(
  path: string,
  init?: RequestInit,
): Promise<TOutcome> {
  let response: Response
  try {
    response = await fetch(path, {
      ...init,
      credentials: 'include',
      headers: {
        'Content-Type': 'application/json',
        ...init?.headers,
      },
    })
  } catch {
    throw new ApiError(t('apiUnreachable'), 0)
  }

  const rawBody = await response.text()

  if (response.status === 200 || response.status === 403) {
    try {
      return JSON.parse(rawBody) as TOutcome
    } catch {
      // Not the outcome shape (e.g. Forbid()'s empty body) — fall through.
    }
  }

  let detail = response.statusText
  try {
    const body = JSON.parse(rawBody)
    detail = body?.title ?? body?.detail ?? JSON.stringify(body)
  } catch {
    // Non-JSON error body; fall back to statusText.
  }
  throw new ApiError(detail || t('requestFailedWithStatus', { status: response.status }), response.status)
}
