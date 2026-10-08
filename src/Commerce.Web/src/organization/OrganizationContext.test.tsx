import { useEffect, useState } from 'react'
import { act, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it } from 'vitest'
import { OrganizationProvider, getSelectedOrganizationId, useOrganizationContext } from './OrganizationContext'

// React runs a child's effects before its parent's, so a screen that fetches
// on mount must already see the selection `apiFetch` reads. These cases pin
// that the header mirror is current at the child's FIRST effect.

const storageKey = 'sysadmin-organization:anonymous'

function FirstEffectProbe({ onFirstEffect }: { onFirstEffect: (id: string | null) => void }) {
  useEffect(() => {
    onFirstEffect(getSelectedOrganizationId())
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])
  return null
}

function SelectThenMount({ onFirstEffect }: { onFirstEffect: (id: string | null) => void }) {
  const { selectOrganization, clearOrganization } = useOrganizationContext()
  const [mounted, setMounted] = useState(false)
  return (
    <>
      <button
        type="button"
        onClick={() => {
          selectOrganization({ id: 'org-a', name: 'Org A' })
          setMounted(true)
        }}
      >
        Open
      </button>
      <button type="button" onClick={clearOrganization}>
        Clear
      </button>
      {mounted && <FirstEffectProbe onFirstEffect={onFirstEffect} />}
    </>
  )
}

afterEach(() => {
  window.localStorage.clear()
})

describe('OrganizationProvider header mirror', () => {
  it('exposes a stored selection to a child fetching on its first mount', () => {
    window.localStorage.setItem(storageKey, JSON.stringify({ id: 'org-stored', name: 'Stored' }))
    const seen: (string | null)[] = []

    render(
      <OrganizationProvider>
        <FirstEffectProbe onFirstEffect={(id) => seen.push(id)} />
      </OrganizationProvider>,
    )

    expect(seen).toEqual(['org-stored'])
  })

  it('exposes a new selection to a screen mounted in the same update', async () => {
    const seen: (string | null)[] = []
    render(
      <OrganizationProvider>
        <SelectThenMount onFirstEffect={(id) => seen.push(id)} />
      </OrganizationProvider>,
    )

    await userEvent.click(screen.getByRole('button', { name: 'Open' }))

    expect(seen).toEqual(['org-a'])
  })

  it('stops sending the selection as soon as it is cleared', async () => {
    render(
      <OrganizationProvider>
        <SelectThenMount onFirstEffect={() => {}} />
      </OrganizationProvider>,
    )
    await userEvent.click(screen.getByRole('button', { name: 'Open' }))

    await act(async () => {
      await userEvent.click(screen.getByRole('button', { name: 'Clear' }))
    })

    expect(getSelectedOrganizationId()).toBeNull()
  })
})
