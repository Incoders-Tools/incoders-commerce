# Agent instructions — incoders-commerce

Two staff-facing layers share one Cloud API: the **web app** (`src/Commerce.Web`) and the **POS desktop**
(`src/Commerce.Pos.Windows` + `src/Commerce.BranchNode`).

## Project skills

| Skill | Trigger | Path |
|---|---|---|
| `cross-layer-parity` | Any change to a feature, screen, column, field, rule or text present in both the web and the POS | `skills/cross-layer-parity/SKILL.md` |

## Always

- Before finishing a change to anything shared by both layers, load `skills/cross-layer-parity/SKILL.md`, check the
  other layer, and keep `docs/architecture/cross-layer-parity.md` up to date.
- Business rules live on the server (`src/Commerce.Domain`, `src/Commerce.Cloud.Api`); both clients call the same endpoints.
- Every new migration under `deploy/db/migrations` is appended verbatim to `deploy/dev/db/init-rls.sql`, and is safe to
  re-run (tests re-apply every migration): widen check constraints only when not already widened.
