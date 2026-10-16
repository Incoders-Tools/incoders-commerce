# Vaca Verde public website

Corporate landing page of Vaca Verde, the butcher shop. It is a separate static site, not part of
`src/Commerce.Web`: the Commerce product is multi-tenant and must not carry one customer's brand. The site only
sends each visitor to the right door of the web app:

| Visitor | Button | Destination |
|---|---|---|
| Clients (with an account or as guests) | Hacer un pedido | `{VITE_APP_URL}/order` |
| Staff and administrators | Ingresar al sistema | `{VITE_APP_URL}/login` |

The client ordering screen answers only when the API has `GuestOrdering__OrganizationId` and
`GuestOrdering__BranchId` set to Vaca Verde's organization and branch (`src/Commerce.Cloud.Api/Tenancy/GuestOrderTarget.cs`).

## Stack

Vite + React + TypeScript + Tailwind CSS 4, the same stack as `src/Commerce.Web`. The build prerenders the page into
`dist/index.html` (`scripts/prerender.mjs`), so the content and the `ButcherShop` structured data are visible without
JavaScript and indexable by search engines.

## Commands

The local stack launcher (`deploy/dev/run-all.bat`) already builds and serves this site on
`http://localhost:4173/`, linked to the app it starts, and opens the browser there. See `deploy/README.md`.

To work on the site alone:

```sh
npm install
npm run dev                                            # http://localhost:5173, buttons point to https://localhost:5443
npm test                                               # Vitest
npm run lint                                           # oxlint
VITE_APP_URL=https://app.example.com npm run build     # static output in dist/
npm run preview                                        # serves dist/
```

`VITE_APP_URL` is the origin of the Commerce web app (https, no path). A build (any mode) without it fails on purpose,
so a site whose buttons go nowhere is never published. Set it in the hosting provider's build settings, or in a local
`.env.production.local` file (ignored by Git).

## Content and brand

- Business facts (phone, address, Instagram, founding year) live in `src/content.ts`, taken from the printed flyer.
- Palette and fonts follow the flyer: paper cream, field green, engraving gold, and the bright logo green used only in
  the access section. Headlines are lowercase Josefin Sans, echoing the "vaca verde" wordmark; body text is Figtree.
- Photos and engravings in `src/assets` were extracted from the flyers supplied by the owner and converted to WebP.
- Opening hours are not published because the flyer does not include them; add them to `src/content.ts` and the
  "dónde encontrarnos" section once the owner confirms them.

## Deploy

Production runs on Railway as the `vaca-verde-site` service, on `vaca-verde.com.ar` and `www.vaca-verde.com.ar`
(`deploy/railway-runbook.md`, section 6c). `Dockerfile` builds the site with `VITE_APP_URL` as a build argument and
serves `dist/` with Caddy (`Caddyfile`, which also answers Railway's `/health` check); `railway.json` holds the
service config, so the service's config file path must be `/sites/vaca-verde/railway.json` and its root directory
`sites/vaca-verde`. Production uses `VITE_APP_URL=https://app.vaca-verde.com.ar`.

Any other static host also works: build command `npm run build` with `VITE_APP_URL` set, output directory `dist`.
