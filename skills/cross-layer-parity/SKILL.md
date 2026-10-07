---
name: cross-layer-parity
description: "Trigger: change to a feature, screen, column, field, rule or text present in both the web app and the POS desktop. Review and align the other layer."
license: Apache-2.0
metadata:
  author: "incoders"
  version: "1.0"
---

## Activation Contract

Use on ANY change to something that exists in both layers: the web app (`src/Commerce.Web`) and the POS desktop
(`src/Commerce.Pos.Windows`, with `src/Commerce.BranchNode` for its local data). Examples: customers, staff/employees,
users and roles, sales and orders, current accounts, cash and payments, prices and categories, labels, columns,
validations, error texts, permissions.

## Hard Rules

- ALWAYS check the other layer before finishing. A web change that is not applied to the POS (or the reverse) needs a
  stated reason; "not checked" is never acceptable.
- Read `docs/architecture/cross-layer-parity.md` first: it maps every shared feature, its files in each layer and its
  known intentional differences.
- Shared behavior lives on the server (Cloud API endpoints, `Commerce.Domain` rules). Both layers call the SAME
  endpoints; never duplicate a business rule in only one client.
- Intentional differences (e.g. POS counter sale vs web order taking) stay, but a fix to their shared parts (a column, a
  validation, a text, a rounding) still goes to both.
- Keep the wording of labels, statuses and error messages identical in both layers unless the map says otherwise.
- Update the parity map in the same change when a feature is added, aligned, or a new intentional difference appears.

## Decision Gates

| Situation | Action |
|-----------|--------|
| Both layers have the feature | Apply the change to both, with tests in both (`tests/Commerce.Integration` for POS/BranchNode, vitest for web) |
| The other layer lacks the feature | Implement it there too, or record it in the map as a gap (with why) |
| The difference is intentional | Keep it; record or confirm it in the map's "Intentional differences" |
| Server-only change (endpoint, rule) | Check both clients still read and show it correctly |

## Execution Steps

1. Find the feature in `docs/architecture/cross-layer-parity.md` (or search both `src/Commerce.Web/src/screens` and
   `src/Commerce.Pos.Windows`).
2. Make the change in the first layer.
3. Open the counterpart files from the map and apply the equivalent change, or note why not.
4. Add or update tests in both layers.
5. Update the map row (status, files, differences).

## Output Contract

Report, per changed feature: the web files, the POS files, and either "aligned in both" or the reason a layer was left
unchanged.

## References

- `docs/architecture/cross-layer-parity.md`: the feature map of both layers and their intentional differences.
