import { useEffect, useState, type FormEvent } from 'react'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
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

/**
 * design.md "Web: `PriceListsScreen` under the existing `RequireAdmin`":
 * three sections on one screen. Reachable only through the existing
 * `RequireAdmin` (App.tsx) — no new guard component. The server's
 * `ManageCatalog` check on every `/pricing` call remains the real gate (see
 * `Endpoints/Pricing.cs`'s remarks on the `ManageUsers`/`ManageCatalog`
 * correction).
 *
 * Suppliers and Import are structural placeholders here: `supplier_price_mappings`
 * and the `price_import_*` tables/endpoints belong to Work Unit 9 and do not
 * exist yet.
 */
export function PriceListsScreen() {
  const [tab, setTab] = useState<Tab>('prices')
  const [priceLists, setPriceLists] = useState<PriceListRecord[]>([])
  const [presentations, setPresentations] = useState<PresentationRecord[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [creatingDefault, setCreatingDefault] = useState(false)
  const [publishingFor, setPublishingFor] = useState<string | null>(null)

  const refresh = async () => {
    setLoading(true)
    setError(null)
    try {
      const [lists, items] = await Promise.all([listPriceLists(), listPresentations()])
      setPriceLists(lists)
      setPresentations(items)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Unexpected error loading price lists.')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    void refresh()
  }, [])

  const defaultPriceList = priceLists.find((list) => list.isDefault) ?? null

  const handleCreateDefault = async () => {
    setError(null)
    try {
      const created = await createPriceList({ name: 'Default', isDefault: true })
      setPriceLists((current) => [...current, created])
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Unexpected error creating the default price list.')
    }
  }

  return (
    <Card className="mx-auto mt-8 w-full max-w-3xl">
      <CardHeader>
        <CardTitle>Price lists</CardTitle>
        <nav className="flex gap-2">
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
      </CardHeader>
      <CardContent>
        {error && (
          <p role="alert" className="text-sm text-red-600">
            {error}
          </p>
        )}

        {tab === 'prices' && (
          <PricesTab
            loading={loading}
            defaultPriceList={defaultPriceList}
            presentations={presentations}
            creatingDefault={creatingDefault}
            setCreatingDefault={setCreatingDefault}
            onCreateDefault={handleCreateDefault}
            publishingFor={publishingFor}
            setPublishingFor={setPublishingFor}
          />
        )}

        {tab === 'suppliers' && (
          <p className="text-sm text-neutral-600">
            Supplier mappings (column mapping for Excel imports) land here in a future release — the
            `supplier_price_mappings` table does not exist yet.
          </p>
        )}

        {tab === 'import' && <ImportTab />}
      </CardContent>
    </Card>
  )
}

function PricesTab({
  loading,
  defaultPriceList,
  presentations,
  onCreateDefault,
  publishingFor,
  setPublishingFor,
}: {
  loading: boolean
  defaultPriceList: PriceListRecord | null
  presentations: PresentationRecord[]
  creatingDefault: boolean
  setCreatingDefault: (value: boolean) => void
  onCreateDefault: () => void | Promise<void>
  publishingFor: string | null
  setPublishingFor: (presentationId: string | null) => void
}) {
  if (loading) {
    return <p>Loading…</p>
  }

  if (defaultPriceList === null) {
    return (
      <div className="flex flex-col gap-3">
        <p>No default price list exists yet for this organization.</p>
        <Button onClick={() => void onCreateDefault()}>Create default price list</Button>
      </div>
    )
  }

  if (presentations.length === 0) {
    return <p>No presentations yet.</p>
  }

  return (
    <ul className="flex flex-col gap-3">
      {presentations.map((presentation) => (
        <li key={presentation.id} className="border-b border-neutral-200 pb-3">
          <div className="flex items-center justify-between">
            <div>
              <p className="font-medium">{presentation.name}</p>
              <p className="text-xs text-neutral-500">{presentation.identificationCode ?? 'No code'}</p>
            </div>
            <div className="flex items-center gap-2">
              <PriceHistory priceListId={defaultPriceList.id} presentationId={presentation.id} />
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
              priceListId={defaultPriceList.id}
              presentationId={presentation.id}
              onCancel={() => setPublishingFor(null)}
              onPublished={() => setPublishingFor(null)}
            />
          )}
        </li>
      ))}
    </ul>
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
        <p role="alert" className="text-sm text-red-600">
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
        <p role="alert" className="text-sm text-red-600">
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
              className="h-9 rounded-md border border-neutral-300 px-2 text-sm"
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
      <p className="text-sm text-neutral-600">No supplier mapping exists yet — configure one before importing.</p>
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
        <p role="alert" className="text-sm text-red-600">
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
