import { useEffect, useMemo, useState, type FormEvent } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { DataToolbar } from '@/components/data/DataToolbar'
import { DataView, type DataViewColumn } from '@/components/data/DataView'
import { PageHeader } from '@/components/data/PageHeader'
import { useViewPreference } from '@/components/data/useViewPreference'
import { FormPage } from '@/components/layout/FormPage'
import { PriceHistory } from './PriceHistory'
import { ImportReviewTable, type ImportReviewRow } from './ImportReviewTable'
import { listPresentations } from '@/api/catalog'
import {
  appendEntry,
  commitImport,
  createPriceList,
  createSupplierMapping,
  getImportBatch,
  listPriceLists,
  listSupplierMappings,
  rejectImport,
  uploadImport,
} from '@/api/pricing'
import { ApiError } from '@/api/client'
import type { PresentationRecord, PriceListRecord, SupplierPriceMappingRecord } from '@/api/types'

type Tab = 'prices' | 'suppliers' | 'import'

function formatCreatedAt(value: string): string {
  const parsed = new Date(value)
  return Number.isNaN(parsed.getTime()) ? '—' : parsed.toLocaleDateString()
}

/**
 * design.md "Web: `PriceListsScreen` under the existing `RequireAdmin`":
 * three sections on one screen. Reachable only through the existing
 * `RequireAdmin` (App.tsx `/app/price-lists`) — no new guard component. The
 * server's `ManageCatalog` check on every `/pricing` call remains the real
 * gate (see `Endpoints/Pricing.cs`'s remarks on the
 * `ManageUsers`/`ManageCatalog` correction).
 *
 * T4c: migrated onto the shared `components/data/*` layer, following
 * `CatalogScreen.tsx`. The screen carries TWO collections, so only the
 * primary one — the organization's price lists — goes through `DataView`
 * (columns from `PriceListRecord`, `view:price-lists` preference, client-side
 * search over what `GET /pricing/price-lists` already returned). The prices
 * held BY the selected list are a nested surface, not a second table/card
 * grid: one view switch cannot sensibly own two grids, and the nested
 * surface is per-presentation history plus a publish form rather than a
 * flat record list.
 *
 * T9: "Manage prices" used to reveal that nested surface as a bordered
 * `<section>` boxed under the list — another instance of the "embedded
 * modal" look the user complained about. Clicking it now swaps the whole
 * screen to a full-screen `FormPage` detail page (`managingListId`),
 * following the same state-swap `CatalogScreen`'s "Edit code" and
 * `CustomersScreen`'s create/edit use, instead of always auto-opening the
 * default list's entries under the table. Price history stays a full-width
 * expandable section WITHIN that detail page rather than its own page: it
 * is a small per-presentation lookup (a handful of rows), not a task that
 * deserves its own navigation hop, and giving it a separate screen would
 * add a back-and-forth for something meant to be glanced at while managing
 * prices.
 */
export function PriceListsScreen() {
  const [tab, setTab] = useState<Tab>('prices')
  const [priceLists, setPriceLists] = useState<PriceListRecord[]>([])
  const [presentations, setPresentations] = useState<PresentationRecord[]>([])
  const [loading, setLoading] = useState(true)
  // Split exactly as T4b's screens do: only a failed LOAD may tell the
  // operator the collection could not be read. A failed action says nothing
  // about whether price lists exist.
  const [loadError, setLoadError] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  // Set only by an explicit "Manage prices" click — the detail page is
  // opened, never auto-shown for the default list (see the T9 remark above).
  const [managingListId, setManagingListId] = useState<string | null>(null)
  const [publishingFor, setPublishingFor] = useState<string | null>(null)
  const [search, setSearch] = useState('')
  const [view, setView] = useViewPreference('price-lists')

  const refresh = async () => {
    setLoading(true)
    setLoadError(null)
    try {
      const [lists, items] = await Promise.all([listPriceLists(), listPresentations()])
      setPriceLists(lists)
      setPresentations(items)
    } catch (err) {
      setLoadError(err instanceof ApiError ? err.message : 'Unexpected error loading price lists.')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    void refresh()
  }, [])

  const defaultPriceList = priceLists.find((list) => list.isDefault) ?? null
  const managingList = priceLists.find((list) => list.id === managingListId) ?? null

  const handleCreateDefault = async () => {
    setActionError(null)
    try {
      const created = await createPriceList({ name: 'Default', isDefault: true })
      setPriceLists((current) => [...current, created])
    } catch (err) {
      setActionError(err instanceof ApiError ? err.message : 'Unexpected error creating the default price list.')
    }
  }

  const trimmedSearch = search.trim().toLowerCase()
  const visiblePriceLists = useMemo(() => {
    if (trimmedSearch === '') return priceLists
    return priceLists.filter((list) => list.name.toLowerCase().includes(trimmedSearch))
  }, [priceLists, trimmedSearch])

  const columns: DataViewColumn<PriceListRecord>[] = [
    { key: 'name', header: 'Name', cell: (list) => list.name },
    // "Is default", not "Default": a list is also commonly NAMED "Default",
    // and a header identical to a cell value elsewhere in the same table
    // makes both the screen and its tests ambiguous.
    { key: 'isDefault', header: 'Is default', cell: (list) => (list.isDefault ? 'Yes' : 'No') },
    {
      key: 'createdAtUtc',
      header: 'Created',
      cell: (list) => formatCreatedAt(list.createdAtUtc),
      hideOnMobile: true,
    },
  ]

  if (managingList) {
    return (
      <PriceListDetail
        priceList={managingList}
        presentations={presentations}
        publishingFor={publishingFor}
        setPublishingFor={setPublishingFor}
        onBack={() => {
          setManagingListId(null)
          setPublishingFor(null)
        }}
      />
    )
  }

  return (
    <section className="flex w-full flex-col gap-6">
      <PageHeader
        title="Price lists"
        description="Published prices per presentation, and the supplier import that feeds them."
        actions={
          !loading && loadError === null && defaultPriceList === null ? (
            <Button onClick={() => void handleCreateDefault()}>Create default price list</Button>
          ) : null
        }
      />

      {loadError && (
        <p role="alert" className="rounded-md border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {loadError}
        </p>
      )}
      {actionError && (
        <p role="alert" className="rounded-md border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {actionError}
        </p>
      )}

      <nav aria-label="Price list sections" className="flex gap-2">
        <Button variant={tab === 'prices' ? 'default' : 'outline'} size="sm" onClick={() => setTab('prices')}>
          Prices
        </Button>
        <Button variant={tab === 'suppliers' ? 'default' : 'outline'} size="sm" onClick={() => setTab('suppliers')}>
          Suppliers
        </Button>
        <Button variant={tab === 'import' ? 'default' : 'outline'} size="sm" onClick={() => setTab('import')}>
          Import
        </Button>
      </nav>

      {tab === 'prices' && (
        <>
          <DataToolbar
            searchValue={search}
            onSearchChange={setSearch}
            searchLabel="Search price lists"
            searchPlaceholder="Search by name…"
            view={view}
            onViewChange={setView}
          />

          <DataView
            items={visiblePriceLists}
            columns={columns}
            getRowKey={(list) => list.id}
            view={view}
            loading={loading}
            loadErrorMessage={loadError ? 'The price lists could not be loaded.' : null}
            emptyMessage={
              priceLists.length === 0 ? 'No price lists yet.' : 'No price lists match this search.'
            }
            renderActions={(list) => (
              <Button
                type="button"
                variant="outline"
                size="sm"
                onClick={() => {
                  setManagingListId(list.id)
                  setPublishingFor(null)
                }}
              >
                Manage prices
              </Button>
            )}
          />
        </>
      )}

      {tab === 'suppliers' && (
        <p className="text-sm text-muted-foreground">
          Supplier mappings (column mapping for Excel imports) land here in a future release — the
          `supplier_price_mappings` table does not exist yet.
        </p>
      )}

      {tab === 'import' && <ImportTab />}
    </section>
  )
}

/**
 * T9: the nested surface — the price entries (`PriceListEntryRecord`) the
 * managed list holds, reached per presentation through `PriceHistory`, plus
 * the publish form — as its own full-screen `FormPage`, reached only by
 * clicking "Manage prices" (`PriceListsScreen`'s `managingListId`).
 * Deliberately NOT a second `DataView`: one view switch cannot sensibly own
 * two grids, and this nested surface is per-presentation history plus a
 * publish form rather than a flat record list (see the screen's own remark
 * above).
 */
function PriceListDetail({
  priceList,
  presentations,
  publishingFor,
  setPublishingFor,
  onBack,
}: {
  priceList: PriceListRecord
  presentations: PresentationRecord[]
  publishingFor: string | null
  setPublishingFor: (presentationId: string | null) => void
  onBack: () => void
}) {
  return (
    <FormPage
      title={`Prices in ${priceList.name}`}
      description="Each presentation's published entries, newest effective date first."
      onBack={onBack}
      backLabel="Back to price lists"
    >
      <div data-testid="price-list-entries" className="flex w-full flex-col gap-3">
        {presentations.length === 0 ? (
          <p className="text-sm text-muted-foreground">No presentations yet.</p>
        ) : (
          <ul className="flex flex-col gap-3">
            {presentations.map((presentation) => (
              <li key={presentation.id} className="border-b border-border pb-3 last:border-0 last:pb-0">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <div className="min-w-0">
                    <p className="font-medium break-words">{presentation.name}</p>
                    <p className="text-xs text-muted-foreground">{presentation.identificationCode ?? 'No code'}</p>
                  </div>
                  <div className="flex items-center gap-2">
                    <PriceHistory priceListId={priceList.id} presentationId={presentation.id} />
                    <Button
                      type="button"
                      variant="outline"
                      size="sm"
                      onClick={() => setPublishingFor(publishingFor === presentation.id ? null : presentation.id)}
                    >
                      New price
                    </Button>
                  </div>
                </div>
                {publishingFor === presentation.id && (
                  <NewPriceForm
                    priceListId={priceList.id}
                    presentationId={presentation.id}
                    onCancel={() => setPublishingFor(null)}
                    onPublished={() => setPublishingFor(null)}
                  />
                )}
              </li>
            ))}
          </ul>
        )}
      </div>
    </FormPage>
  )
}

function NewPriceForm({
  priceListId,
  presentationId,
  onCancel,
  onPublished,
}: {
  priceListId: string
  presentationId: string
  onCancel: () => void
  onPublished: () => void
}) {
  const [unitPrice, setUnitPrice] = useState('')
  const [effectiveFrom, setEffectiveFrom] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault()
    setError(null)
    setSubmitting(true)
    try {
      await appendEntry(priceListId, {
        presentationId,
        unitPrice: Number(unitPrice),
        effectiveFrom,
      })
      onPublished()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Unexpected error publishing the price.')
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <form className="mt-2 flex flex-wrap items-end gap-2" onSubmit={handleSubmit}>
      <div className="flex flex-col gap-1.5">
        {/* Neutral label on purpose. `openspec/changes/commerce-price-composition`
            (proposed, NOT implemented) turns `PriceListEntry.unitPrice` into a
            BASE price, with the sellable price derived from the list's rate
            components (IVA, IB, freight, markup). When that lands, this label —
            and `PriceHistory`'s rendering of the same field — is what has to
            change. Until then the screen shows the stored figure only, with no
            derived column, tax breakdown or total. */}
        <Label htmlFor={`unitPrice-${presentationId}`}>Unit price</Label>
        <Input
          id={`unitPrice-${presentationId}`}
          type="number"
          step="0.01"
          value={unitPrice}
          onChange={(e) => setUnitPrice(e.target.value)}
          required
        />
      </div>
      <div className="flex flex-col gap-1.5">
        <Label htmlFor={`effectiveFrom-${presentationId}`}>Effective from</Label>
        <Input
          id={`effectiveFrom-${presentationId}`}
          type="date"
          value={effectiveFrom}
          onChange={(e) => setEffectiveFrom(e.target.value)}
          required
        />
      </div>
      {error && (
        <p role="alert" className="text-sm text-destructive">
          {error}
        </p>
      )}
      <Button type="submit" size="sm" disabled={submitting}>
        {submitting ? 'Publishing…' : 'Publish'}
      </Button>
      <Button type="button" variant="outline" size="sm" onClick={onCancel} disabled={submitting}>
        Cancel
      </Button>
    </form>
  )
}

function toReviewRows(rows: { rowNumber: number; rawCode: string | null; presentationId: string | null; currentPrice: number | null; proposedPrice: number | null; matchStatus: ImportReviewRow['status'] }[]): ImportReviewRow[] {
  return rows.map((row) => ({
    rowNumber: row.rowNumber,
    rawCode: row.rawCode ?? '',
    // The review table only needs a presence signal here; the endpoint's
    // response does not carry the matched presentation's display name, so a
    // matched row shows its id — good enough for admin review without a
    // second round-trip per row.
    matchedItemName: row.presentationId,
    currentPrice: row.currentPrice,
    proposedPrice: row.proposedPrice ?? 0,
    status: row.matchStatus,
  }))
}

/**
 * design.md "Import lifecycle": upload -> review -> commit/reject, wired to
 * the real Work Unit 9 endpoints. Uploading requires a saved supplier
 * mapping (design.md "Per-Supplier Saved Column Mapping"); this tab offers a
 * minimal inline create form when none exists yet, rather than duplicating
 * a full Suppliers CRUD screen the design does not otherwise require here.
 */
function ImportTab() {
  const [mappings, setMappings] = useState<SupplierPriceMappingRecord[] | null>(null)
  const [selectedMappingId, setSelectedMappingId] = useState('')
  const [file, setFile] = useState<File | null>(null)
  const [uploading, setUploading] = useState(false)
  const [committing, setCommitting] = useState(false)
  const [rows, setRows] = useState<ImportReviewRow[]>([])
  const [batchId, setBatchId] = useState<string | null>(null)
  const [resolvedStatus, setResolvedStatus] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    void listSupplierMappings()
      .then((list) => {
        setMappings(list)
        if (list.length > 0) {
          setSelectedMappingId(list[0].id)
        }
      })
      .catch((err) => setError(err instanceof ApiError ? err.message : 'Unexpected error loading supplier mappings.'))
  }, [])

  const handleUpload = async () => {
    if (!file || !selectedMappingId) {
      return
    }
    setError(null)
    setUploading(true)
    setResolvedStatus(null)
    try {
      const batch = await uploadImport(selectedMappingId, file)
      const detail = await getImportBatch(batch.id)
      setBatchId(batch.id)
      setRows(toReviewRows(detail.rows))
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Unexpected error uploading the file.')
    } finally {
      setUploading(false)
    }
  }

  const handleCommit = async () => {
    if (!batchId) {
      return
    }
    setError(null)
    setCommitting(true)
    try {
      const result = await commitImport(batchId)
      setResolvedStatus(result.status)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Unexpected error committing the batch.')
    } finally {
      setCommitting(false)
    }
  }

  const handleReject = async () => {
    if (!batchId) {
      return
    }
    setError(null)
    setCommitting(true)
    try {
      const result = await rejectImport(batchId)
      setResolvedStatus(result.status)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Unexpected error rejecting the batch.')
    } finally {
      setCommitting(false)
    }
  }

  if (mappings === null) {
    return <p>Loading…</p>
  }

  return (
    <div className="flex flex-col gap-4">
      {error && (
        <p role="alert" className="text-sm text-destructive">
          {error}
        </p>
      )}

      {mappings.length === 0 ? (
        <CreateSupplierMappingInline onCreated={(created) => {
          setMappings([created])
          setSelectedMappingId(created.id)
        }} />
      ) : (
        <div className="flex flex-wrap items-end gap-2">
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="import-supplier">Supplier</Label>
            <select
              id="import-supplier"
              className="h-9 rounded-md border border-input px-2 text-sm"
              value={selectedMappingId}
              onChange={(e) => setSelectedMappingId(e.target.value)}
            >
              {mappings.map((m) => (
                <option key={m.id} value={m.id}>
                  {m.supplierName}
                </option>
              ))}
            </select>
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="import-file">File</Label>
            <input
              id="import-file"
              type="file"
              accept=".xlsx"
              onChange={(e) => setFile(e.target.files?.[0] ?? null)}
            />
          </div>
          <Button type="button" size="sm" onClick={() => void handleUpload()} disabled={!file || uploading}>
            {uploading ? 'Uploading…' : 'Upload'}
          </Button>
        </div>
      )}

      {resolvedStatus && <p>Batch {resolvedStatus.toLowerCase()}.</p>}

      <ImportReviewTable
        rows={rows}
        onCommit={() => void handleCommit()}
        onReject={() => void handleReject()}
        committing={committing}
      />
    </div>
  )
}

function CreateSupplierMappingInline({ onCreated }: { onCreated: (created: SupplierPriceMappingRecord) => void }) {
  const [supplierName, setSupplierName] = useState('')
  const [sheetName, setSheetName] = useState('')
  const [headerRow, setHeaderRow] = useState('1')
  const [codeColumn, setCodeColumn] = useState('')
  const [priceColumn, setPriceColumn] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault()
    setError(null)
    setSubmitting(true)
    try {
      const created = await createSupplierMapping({
        supplierName,
        sheetName,
        headerRow: Number(headerRow),
        codeColumn,
        priceColumn,
      })
      onCreated(created)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Unexpected error saving the supplier mapping.')
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <form className="flex flex-col gap-3" onSubmit={handleSubmit}>
      <p className="text-sm text-muted-foreground">No supplier mapping exists yet — configure one before importing.</p>
      <div className="flex flex-wrap gap-2">
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="supplier-name">Supplier name</Label>
          <Input id="supplier-name" value={supplierName} onChange={(e) => setSupplierName(e.target.value)} required />
        </div>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="sheet-name">Sheet name</Label>
          <Input id="sheet-name" value={sheetName} onChange={(e) => setSheetName(e.target.value)} required />
        </div>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="header-row">Header row</Label>
          <Input id="header-row" type="number" min={1} value={headerRow} onChange={(e) => setHeaderRow(e.target.value)} required />
        </div>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="code-column">Code column</Label>
          <Input id="code-column" value={codeColumn} onChange={(e) => setCodeColumn(e.target.value)} required />
        </div>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="price-column">Price column</Label>
          <Input id="price-column" value={priceColumn} onChange={(e) => setPriceColumn(e.target.value)} required />
        </div>
      </div>
      {error && (
        <p role="alert" className="text-sm text-destructive">
          {error}
        </p>
      )}
      <div>
        <Button type="submit" size="sm" disabled={submitting}>
          {submitting ? 'Saving…' : 'Save mapping'}
        </Button>
      </div>
    </form>
  )
}
