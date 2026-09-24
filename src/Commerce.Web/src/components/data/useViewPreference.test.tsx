import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it } from 'vitest'
import { useViewPreference } from './useViewPreference'

/**
 * T4: per-screen view preference persisted in localStorage, following the
 * tolerant read/write pattern `theme/ThemeProvider.tsx` already uses
 * (try/catch on both sides, corrupt values fall back to the default).
 */
function Harness({ screenKey }: { screenKey: string }) {
  const [view, setView] = useViewPreference(screenKey)
  return (
    <div>
      <span data-testid="view">{view}</span>
      <button type="button" onClick={() => setView('cards')}>
        pick cards
      </button>
      <button type="button" onClick={() => setView('table')}>
        pick table
      </button>
    </div>
  )
}

describe('useViewPreference', () => {
  afterEach(() => {
    window.localStorage.clear()
  })

  it('defaults to the table view when nothing is stored', () => {
    render(<Harness screenKey="catalog" />)

    expect(screen.getByTestId('view')).toHaveTextContent('table')
  })

  it('persists the picked view under a per-screen key', async () => {
    const user = userEvent.setup()
    render(<Harness screenKey="catalog" />)

    await user.click(screen.getByRole('button', { name: 'pick cards' }))

    expect(screen.getByTestId('view')).toHaveTextContent('cards')
    expect(window.localStorage.getItem('view:catalog')).toBe('cards')
  })

  it('restores the persisted view on remount', async () => {
    const user = userEvent.setup()
    const first = render(<Harness screenKey="catalog" />)
    await user.click(screen.getByRole('button', { name: 'pick cards' }))
    first.unmount()

    render(<Harness screenKey="catalog" />)

    expect(screen.getByTestId('view')).toHaveTextContent('cards')
  })

  it('keeps preferences isolated per screen', async () => {
    const user = userEvent.setup()
    const first = render(<Harness screenKey="catalog" />)
    await user.click(screen.getByRole('button', { name: 'pick cards' }))
    first.unmount()

    render(<Harness screenKey="customers" />)

    expect(screen.getByTestId('view')).toHaveTextContent('table')
  })

  it('falls back to the default when the stored value is corrupt', () => {
    window.localStorage.setItem('view:catalog', 'not-a-view')

    render(<Harness screenKey="catalog" />)

    expect(screen.getByTestId('view')).toHaveTextContent('table')
  })
})
