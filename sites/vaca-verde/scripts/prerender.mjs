// Renders the page to static HTML inside dist/index.html, so the content is
// indexable and visible before (or without) JavaScript. Runs after the client
// and SSR builds; the SSR bundle is a build intermediate and is removed.
import { readFile, rm, writeFile } from 'node:fs/promises'
import path from 'node:path'
import { pathToFileURL } from 'node:url'

const root = path.resolve(import.meta.dirname, '..')
const indexPath = path.join(root, 'dist', 'index.html')
const ssrDir = path.join(root, 'dist-ssr')

const { render } = await import(pathToFileURL(path.join(ssrDir, 'entry-server.js')).href)
const template = await readFile(indexPath, 'utf8')
const placeholder = '<!--app-html-->'
if (!template.includes(placeholder)) {
  throw new Error(`dist/index.html has no ${placeholder} placeholder.`)
}

// A function replacement: the rendered HTML is inserted as is, never read for `$&`-style patterns.
const html = render()
await writeFile(indexPath, template.replace(placeholder, () => html))
await rm(ssrDir, { recursive: true, force: true })
console.log('Prerendered dist/index.html')
