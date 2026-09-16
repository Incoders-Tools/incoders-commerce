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
        ...init?.headers,
      },
    })
  } catch {
    // Network-level failure: API unreachable (spec.md "API unreachable"
    // scenario) — surfaced as a typed error the caller renders visibly.
    throw new ApiError('Commerce.Cloud.Api is unreachable.', 0)
  }

  if (!response.ok) {
    let detail = response.statusText
    try {
      const body = await response.json()
      detail = body?.title ?? body?.detail ?? JSON.stringify(body)
    } catch {
      // Non-JSON error body; fall back to statusText.
    }
    throw new ApiError(detail || `Request failed with status ${response.status}`, response.status)
  }

  if (response.status === 204) {
    return undefined as TResponse
  }

  return (await response.json()) as TResponse
}
