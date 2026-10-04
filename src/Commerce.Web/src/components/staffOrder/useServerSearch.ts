import { useEffect, useRef, useState } from 'react'
import { useDebouncedValue } from '@/lib/useDebouncedValue'

const SEARCH_DEBOUNCE_MS = 300

export interface SearchState<T> {
  status: 'idle' | 'loading' | 'ready' | 'error'
  results: T[]
}

/** The server's answer for one exact term. */
interface Answer<T> {
  term: string
  failed: boolean
  results: T[]
}

/**
 * Debounced server search for the take order screen's pickers: nothing is asked while the text is blank, and an
 * answer to an older text never replaces the answer to the latest one. `search` must be stable (a module-level
 * API function), or every render would search again.
 */
export function useServerSearch<T>(text: string, search: (term: string) => Promise<T[]>): SearchState<T> {
  const term = useDebouncedValue(text.trim(), SEARCH_DEBOUNCE_MS)
  const [answer, setAnswer] = useState<Answer<T> | null>(null)
  const latestRequest = useRef(0)

  useEffect(() => {
    const request = ++latestRequest.current
    if (term === '') return
    search(term)
      .then((results) => {
        if (request === latestRequest.current) setAnswer({ term, failed: false, results })
      })
      .catch(() => {
        if (request === latestRequest.current) setAnswer({ term, failed: true, results: [] })
      })
  }, [term, search])

  // The text was cleared (e.g. after picking a result): hide the old results right away, not after the debounce.
  if (text.trim() === '' || term === '') return { status: 'idle', results: [] }
  // Still typing or waiting for the server: keep showing the previous results meanwhile.
  if (answer === null || answer.term !== term || term !== text.trim()) {
    return { status: 'loading', results: answer?.results ?? [] }
  }
  return answer.failed ? { status: 'error', results: [] } : { status: 'ready', results: answer.results }
}
