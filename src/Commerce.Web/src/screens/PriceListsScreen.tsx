import { useCallback, useEffect, useMemo, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { DataToolbar } from '@/components/data/DataToolbar'
import { DataView, type DataViewColumn } from '@/components/data/DataView'
import { PageHeader } from '@/components/data/PageHeader'
import { useViewPreference } from '@/components/data/useViewPreference'
import { PriceListBreakdownPage } from './PriceListBreakdownPage'
import { CompositionForm } from './CompositionForm'
import { CopyPriceListForm } from './CopyPriceListForm'
import { DiscardEditsDialog, PriceEditorTab } from './PriceEditorTab'
import { ImportReviewTable, type ImportReviewRow } from './ImportReviewTable'
import { listPresentations } from '@/api/catalog'
import {
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
import { useMissingBranch } from '@/branch/useMissingBranch'
import type { PresentationRecord, PriceListRecord, SupplierPriceMappingRecord } from '@/api/types'

type Tab = 'prices' | 'edit' | 'suppliers' | 'import'

/** The composition pages of one list, each a full-screen page. */
type CompositionPage = { kind: 'breakdown' | 'composition' | 'copy'; listId: string; notice?: string }

function formatCreatedAt(value: string): string {
  const parsed = new Date(value)
  return Number.isNaN(parsed.getTime()) ? '—' : parsed.toLocaleDateString('es-AR')
}

/**
 * design.md "Web: `PriceListsScreen` under the existing `RequireAdmin`":
 * four sections on one screen. Reachable only through the existing
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
 * held BY a list live in their own tab, not in a second table/card grid
 * under the same view switch.
 *
 * T9 moved the per-presentation prices to a full-screen detail page. The
 * price editing redesign (odd/tasks/price-editing-and-desktop-polish.md, T3)
 * replaced that page with the "Editar precios" tab (`PriceEditorTab`): a grid
 * of every product of one list, edited one by one or remarked by a
 * percentage and published as one batch. "Gestionar precios" on a list row
 * now opens that tab with the row's list preselected.
 */
export function PriceListsScreen() {
  const { t } = useTranslation('priceLists')
  // Price lists (and every page reached from here) are branch-owned: without
  // a selected branch the API answers 400 `branch-selection-required`, so the
  // screen asks for one instead.
  const missingBranch = useMissingBranch()
  const [tab, setTab] = useState<Tab>('prices')
  const [priceLists, setPriceLists] = useState<PriceListRecord[]>([])
  const [presentations, setPresentations] = useState<PresentationRecord[]>([])
  const [loading, setLoading] = useState(!missingBranch)
  // Split exactly as T4b's screens do: only a failed LOAD may tell the
  // operator the collection could not be read. A failed action says nothing
  // about whether price lists exist.
  const [loadError, setLoadError] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  // The list the "Editar precios" tab edits; null until chosen there, or preselected by "Gestionar precios".
  const [editorListId, setEditorListId] = useState<string | null>(null)
  // Unpublished edits in "Editar precios": leaving the tab asks first, and the tab waits in `pendingTab`.
  const [editorDirtyCount, setEditorDirtyCount] = useState(0)
  const [pendingTab, setPendingTab] = useState<Tab | null>(null)
  const [page, setPage] = useState<CompositionPage | null>(null)
  const [search, setSearch] = useState('')
  const [view, setView] = useViewPreference('price-lists')

  const refresh = useCallback(async () => {
    if (missingBranch) return
    setLoading(true)
    setLoadError(null)
    try {
      const [lists, items] = await Promise.all([listPriceLists(), listPresentations()])
      setPriceLists(lists)
      setPresentations(items)
    } catch (err) {
      setLoadError(err instanceof ApiError ? err.message : t('errors.unexpectedLoad'))
    } finally {
      setLoading(false)
    }
  }, [t, missingBranch])

  useEffect(() => {
    void refresh()
  }, [refresh])

  const defaultPriceList = priceLists.find((list) => list.isDefault) ?? null
  const editedListId = editorListId ?? defaultPriceList?.id ?? priceLists[0]?.id ?? null

  const handleCreateDefault = async () => {
    setActionError(null)
    try {
      const created = await createPriceList({ name: 'Default', isDefault: true })
      setPriceLists((current) => [...current, created])
    } catch (err) {
      setActionError(err instanceof ApiError ? err.message : t('errors.unexpectedCreateDefault'))
    }
  }

  const trimmedSearch = search.trim().toLowerCase()
  const visiblePriceLists = useMemo(() => {
    if (trimmedSearch === '') return priceLists
    return priceLists.filter((list) => list.name.toLowerCase().includes(trimmedSearch))
  }, [priceLists, trimmedSearch])

  const columns: DataViewColumn<PriceListRecord>[] = [
    { key: 'name', header: t('columns.name'), cell: (list) => list.name },
    // "Is default", not "Default": a list is also commonly NAMED "Default",
    // and a header identical to a cell value elsewhere in the same table
    // makes both the screen and its tests ambiguous.
    {
      key: 'isDefault',
      header: t('columns.isDefault'),
      cell: (list) => (list.isDefault ? t('columns.yes') : t('columns.no')),
    },
    {
      key: 'createdAtUtc',
      header: t('columns.created'),
      cell: (list) => formatCreatedAt(list.createdAtUtc),
      hideOnMobile: true,
    },
  ]

  if (missingBranch) {
    return (
      <section className="flex w-full flex-col gap-6">
        <PageHeader title={t('title')} description={t('description')} />
        <p role="status" className="rounded-lg border border-border bg-card px-4 py-10 text-center text-sm text-muted-foreground">
          {t('branchRequired')}
        </p>
      </section>
    )
  }

  const pageList = priceLists.find((list) => list.id === page?.listId) ?? null
  if (page && pageList) {
    const toBreakdown = () => setPage({ kind: 'breakdown', listId: pageList.id })
    if (page.kind === 'composition') {
      return <CompositionForm priceList={pageList} onBack={toBreakdown} onPublished={toBreakdown} />
    }
    if (page.kind === 'copy') {
      return (
        <CopyPriceListForm
          source={pageList}
          priceLists={priceLists}
          onBack={() => setPage(null)}
          onCopied={(result) => {
            setPriceLists((current) => [...current, result.priceList])
            setPage({
              kind: 'breakdown',
              listId: result.priceList.id,
              notice: t('breakdown.copied', { count: result.entriesCopied }),
            })
          }}
        />
      )
    }
    return (
      <PriceListBreakdownPage
        // Another list (after a copy) is a fresh page: its own date, history and floor state.
        key={pageList.id}
        priceList={pageList}
        priceLists={priceLists}
        notice={page.notice}
        onBack={() => setPage(null)}
        onEditComposition={() => setPage({ kind: 'composition', listId: pageList.id })}
        onListChanged={(changed) =>
          setPriceLists((current) => current.map((list) => (list.id === changed.id ? changed : list)))
        }
      />
    )
  }

  return (
    <section className="flex w-full flex-col gap-6">
      <PageHeader
        title={t('title')}
        description={t('description')}
        actions={
          !loading && loadError === null && defaultPriceList === null ? (
            <Button onClick={() => void handleCreateDefault()}>{t('createDefault')}</Button>
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

      <nav aria-label={t('sectionsNav.label')} className="flex gap-2">
        {(['prices', 'edit', 'suppliers', 'import'] as const).map((section) => (
          <Button
            key={section}
            variant={tab === section ? 'default' : 'outline'}
            size="sm"
            aria-pressed={tab === section}
            onClick={() => {
              if (tab === 'edit' && section !== 'edit' && editorDirtyCount > 0) setPendingTab(section)
              else setTab(section)
            }}
          >
            {t(`sectionsNav.${section}`)}
          </Button>
        ))}
      </nav>

      {tab === 'prices' && (
        <>
          <DataToolbar
            searchValue={search}
            onSearchChange={setSearch}
            searchLabel={t('search.label')}
            searchPlaceholder={t('search.placeholder')}
            view={view}
            onViewChange={setView}
          />

          <DataView
            items={visiblePriceLists}
            columns={columns}
            getRowKey={(list) => list.id}
            view={view}
            loading={loading}
            loadErrorMessage={loadError ? t('empty.loadError') : null}
            emptyMessage={priceLists.length === 0 ? t('empty.none') : t('empty.noMatch')}
            renderActions={(list) => (
              <>
                <Button type="button" variant="outline" size="sm" onClick={() => setPage({ kind: 'breakdown', listId: list.id })}>
                  {t('actions.composition')}
                </Button>
                <Button type="button" variant="outline" size="sm" onClick={() => setPage({ kind: 'copy', listId: list.id })}>
                  {t('actions.copy')}
                </Button>
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  onClick={() => {
                    setEditorListId(list.id)
                    setTab('edit')
                  }}
                >
                  {t('actions.managePrices')}
                </Button>
              </>
            )}
          />
        </>
      )}

      {tab === 'edit' && (
        <PriceEditorTab
          priceLists={priceLists}
          presentations={presentations}
          priceListId={editedListId}
          onPriceListChange={setEditorListId}
          onDirtyChange={setEditorDirtyCount}
        />
      )}

      {pendingTab !== null && (
        <DiscardEditsDialog
          count={editorDirtyCount}
          onDiscard={() => {
            setTab(pendingTab)
            setPendingTab(null)
          }}
          onKeepEditing={() => setPendingTab(null)}
        />
      )}

      {tab === 'suppliers' && (
        <p className="text-sm text-muted-foreground">
          {t('suppliersPlaceholder')}
        </p>
      )}

      {tab === 'import' && <ImportTab />}
    </section>
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
  const { t } = useTranslation('priceLists')
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
      .catch((err) => setError(err instanceof ApiError ? err.message : t('errors.unexpectedLoadSupplierMappings')))
    // Mount-only: matches the original one-shot fetch; `t` is a stable
    // reference from react-i18next.
    // eslint-disable-next-line react-hooks/exhaustive-deps
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
      setError(err instanceof ApiError ? err.message : t('errors.unexpectedUpload'))
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
      setError(err instanceof ApiError ? err.message : t('errors.unexpectedCommit'))
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
      setError(err instanceof ApiError ? err.message : t('errors.unexpectedReject'))
    } finally {
      setCommitting(false)
    }
  }

  if (mappings === null) {
    return <p>{t('import.loading')}</p>
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
            <Label htmlFor="import-supplier">{t('import.supplierLabel')}</Label>
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
            <Label htmlFor="import-file">{t('import.fileLabel')}</Label>
            <input
              id="import-file"
              type="file"
              accept=".xlsx"
              onChange={(e) => setFile(e.target.files?.[0] ?? null)}
            />
          </div>
          <Button type="button" size="sm" onClick={() => void handleUpload()} disabled={!file || uploading}>
            {uploading ? t('import.uploading') : t('import.upload')}
          </Button>
        </div>
      )}

      {/* `resolvedStatus` is the raw server-returned batch status word
          (e.g. "Committed"/"Rejected"); left untranslated like other
          server-driven values, only the surrounding sentence is localized. */}
      {resolvedStatus && <p>{t('import.batchStatus', { status: resolvedStatus.toLowerCase() })}</p>}

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
  const { t } = useTranslation('priceLists')
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
      setError(err instanceof ApiError ? err.message : t('errors.unexpectedSaveMapping'))
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <form className="flex flex-col gap-3" onSubmit={handleSubmit}>
      <p className="text-sm text-muted-foreground">{t('import.createMapping.explanation')}</p>
      <div className="flex flex-wrap gap-2">
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="supplier-name">{t('import.createMapping.supplierNameLabel')}</Label>
          <Input id="supplier-name" value={supplierName} onChange={(e) => setSupplierName(e.target.value)} required />
        </div>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="sheet-name">{t('import.createMapping.sheetNameLabel')}</Label>
          <Input id="sheet-name" value={sheetName} onChange={(e) => setSheetName(e.target.value)} required />
        </div>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="header-row">{t('import.createMapping.headerRowLabel')}</Label>
          <Input id="header-row" type="number" min={1} value={headerRow} onChange={(e) => setHeaderRow(e.target.value)} required />
        </div>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="code-column">{t('import.createMapping.codeColumnLabel')}</Label>
          <Input id="code-column" value={codeColumn} onChange={(e) => setCodeColumn(e.target.value)} required />
        </div>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="price-column">{t('import.createMapping.priceColumnLabel')}</Label>
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
          {submitting ? t('import.createMapping.saving') : t('import.createMapping.save')}
        </Button>
      </div>
    </form>
  )
}
