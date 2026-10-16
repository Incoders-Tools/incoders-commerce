# Catalog Categories Specification

## Purpose

Define organization-scoped product categories: one shared set per
organization, each with a name and an icon key from a fixed set, managed by
catalog administrators, referenced by every product, and replicated to the
POS so the sale screen can filter its product cards by category.

## Requirements

### Requirement: Organization-Owned Categories

A category MUST belong to exactly one organization and MUST be shared by every
branch of that organization. A category MUST carry a name and an icon key.
Category names MUST be unique within an organization, compared without regard
to letter case or surrounding whitespace. The icon key MUST be one of the
fixed set: `meat`, `poultry`, `fish`, `wine`, `drinks`, `charcoal`, `grocery`,
`cleaning`, `bakery`, `dairy`, `produce`, `generic`. Uniqueness and the icon
set MUST be enforced by the database, not only by application code.

#### Scenario: Duplicate name in one organization is rejected

- GIVEN an organization has a category named "Carnes"
- WHEN a category named " carnes " is created in the same organization
- THEN the request is refused and no second category exists

#### Scenario: The same name may exist in two organizations

- GIVEN organization A has a category named "Carnes"
- WHEN organization B creates a category named "Carnes"
- THEN both categories exist, each visible only to its own organization

#### Scenario: Unknown icon key is rejected

- GIVEN an admin submits an icon key outside the fixed set
- WHEN the category is created or updated
- THEN the request is refused with a validation error and nothing is persisted

### Requirement: Categories Are Isolated Per Organization

Category rows MUST be readable and writable only inside the organization named
by the request's tenant scope, failing closed when no scope is set. A category
MUST NOT require a selected branch, because it is shared by all branches.

#### Scenario: Cross-organization category is invisible

- GIVEN organization A owns a category
- WHEN an admin of organization B lists or edits categories, or addresses that
  category by id
- THEN organization A's category is not listed and the addressed id behaves as
  if it did not exist

### Requirement: Admin Management, Read Access For Staff

Creating, renaming, changing the icon of, and deleting a category MUST require
the catalog-management permission (a business administrator, or a system
administrator acting on a selected organization). Listing categories MUST be
available to any authenticated, non-revoked member of the organization.

#### Scenario: Staff without catalog permission can list but not write

- GIVEN a staff member holds no catalog-management permission
- WHEN they list categories and then try to create one
- THEN the list succeeds and the creation is forbidden

### Requirement: Every Product References One Category Of Its Organization

Every product MUST reference exactly one category of the same organization,
enforced by a database foreign key over organization and category. Creating or
updating a product with a category id that does not belong to the caller's
organization MUST be refused. When a product is created without a category id,
the organization's default category "Sin categoría" MUST be used, creating it
first when it does not yet exist.

#### Scenario: Product with a foreign category is refused

- GIVEN organization B owns a category
- WHEN an admin of organization A creates or updates a product with that
  category id
- THEN the request is refused and the product is unchanged

#### Scenario: Product without a category uses the default

- GIVEN an organization has no categories
- WHEN an admin creates a product without a category id
- THEN a category named "Sin categoría" is created and the product references it

### Requirement: Category Deletion Is Refused While In Use

A category that any product still references MUST NOT be deleted. The refusal
MUST be a clear conflict error naming the reason, and the category MUST remain.
An unused category MAY be deleted.

#### Scenario: Deleting a category with products is refused

- GIVEN a category is referenced by at least one product in any branch
- WHEN an admin deletes it
- THEN the request is refused with a conflict and the category still exists

### Requirement: Existing Products Are Backfilled

The migration that introduces categories MUST create one "Sin categoría"
category for every organization that owns products and point every existing
product of that organization at it, because existing category ids reference
nothing. The migration MUST run in one transaction, be re-runnable without
creating duplicates, and MUST mark the rewritten products as changed so
devices pick up their category.

#### Scenario: Backfill assigns the default category

- GIVEN organizations with products whose category ids reference nothing, and
  an organization without products
- WHEN the migration runs, and runs a second time
- THEN each organization with products has exactly one "Sin categoría"
  category referenced by all its products, and the organization without
  products has no category

### Requirement: Category Changes Reach Devices

The device catalog sync MUST carry each presentation's category id, name and
icon key (nullable for older rows). A change to a product or to its category
MUST cause that product's presentations to be sent again, even when no
presentation row changed.

#### Scenario: Renaming a category re-sends its presentations

- GIVEN a device has already synced a presentation whose product has a category
- WHEN an admin renames that category
- THEN the next sync from the device's previous cursor includes that
  presentation with the new category name

#### Scenario: Editing only a product re-sends its presentations

- GIVEN a device has already synced a presentation
- WHEN an admin changes only the product's name or category
- THEN the next sync from the previous cursor includes that presentation

### Requirement: Web Category Management

The web app MUST give administrators a categories screen to list, create,
rename, change the icon of and delete categories, choosing the icon from the
fixed set, in Spanish. A refused duplicate name or a refused deletion of a
category in use MUST be explained to the operator. The catalog edit page MUST
let an administrator change the category of a presentation's product, offering
only the organization's categories, and MUST still allow saving the
identification code when the categories cannot be loaded.

#### Scenario: Administrator renames a category and changes its icon

- GIVEN an administrator opens the categories screen
- WHEN they edit a category, change its name and pick another icon, and save
- THEN the category is listed with the new name and icon

#### Scenario: Deleting a category in use explains why

- GIVEN a category is used by at least one product
- WHEN an administrator confirms its deletion
- THEN the screen states that products still use it and the category stays listed

### Requirement: POS Category Rail

The POS MUST store each replicated presentation's category id, name and icon
key in its local catalog replica, migrating an existing local database
without losing rows. The sale screen's category rail MUST list "Todos" first
and then the distinct categories present in the local catalog, each with the
glyph for its icon key (a generic glyph for an unknown key), and selecting a
category MUST filter the product cards, combined with the name search. A
category selection that no longer exists after a sync MUST fall back to
"Todos".

#### Scenario: Selecting a category filters the cards

- GIVEN the local catalog holds products of two categories
- WHEN the operator selects one category on the rail and types a name
- THEN only that category's products whose name matches are listed

#### Scenario: Existing local database is migrated

- GIVEN a branch database created before categories were replicated
- WHEN the POS opens it
- THEN its catalog rows are kept, uncategorized, until the next sync
  re-sends them with their category
