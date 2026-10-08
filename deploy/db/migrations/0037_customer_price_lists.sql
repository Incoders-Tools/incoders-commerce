-- Repo-owned, idempotent, forward-only per-environment migration.
--
-- customer-price-lists (T1): every sale is priced from the list that applies to the buyer. Three references:
--
--   * customers.price_list_id                    - the customer's own price list (NULL = "use the organization default").
--   * organizations.default_customer_price_list_id - the list a customer without one of its own is priced from, and
--                                                  the list the web form pre-selects for a new customer.
--   * price_lists.floor_price_list_id            - the list this list must never price below, product by product.
--
-- APPLIED AFTER: 0036_product_soft_delete.sql.
--
-- Scoping rule (decision): customers and organizations are ORGANIZATION scoped while price lists are BRANCH scoped
-- since 0017. A customer (or the organization default) therefore points at ONE price list of its organization, enforced
-- by the composite foreign key (organization_id, id) of 0014 (`price_lists_org_scoped_uk`). Resolution only honours the
-- reference when that list is visible in the branch that is selling; otherwise it falls back to the selling branch's
-- default list (see `BuyerPriceListResolver`). A branch that wants customers priced differently owns its own lists.
-- A floor list stays inside the SAME branch (composite key `price_lists_branch_scoped_uk`, 0017) and is never the list
-- itself; longer cycles are refused by the API.
--
-- Nothing here deletes: price lists keep NO DELETE grant for `app_runtime`, so a referenced list cannot disappear
-- under a customer (the default `NO ACTION` of the foreign keys is the second guard). Table-level grants and the
-- tenant-isolation policies of `customers`, `organizations` and `price_lists` already cover the new columns.
--
-- The whole file runs in ONE transaction and can be re-run safely.
--
-- INVERSE (rollback), shipped as a comment - NOT executed by this file:
--   ALTER TABLE price_lists DROP COLUMN floor_price_list_id;
--   ALTER TABLE organizations DROP COLUMN default_customer_price_list_id;
--   ALTER TABLE customers DROP COLUMN price_list_id;

BEGIN;

ALTER TABLE customers     ADD COLUMN IF NOT EXISTS price_list_id uuid NULL;
ALTER TABLE organizations ADD COLUMN IF NOT EXISTS default_customer_price_list_id uuid NULL;
ALTER TABLE price_lists   ADD COLUMN IF NOT EXISTS floor_price_list_id uuid NULL;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'customers_price_list_org_fk') THEN
        ALTER TABLE customers
            ADD CONSTRAINT customers_price_list_org_fk
            FOREIGN KEY (organization_id, price_list_id) REFERENCES price_lists (organization_id, id);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'organizations_default_customer_price_list_fk') THEN
        ALTER TABLE organizations
            ADD CONSTRAINT organizations_default_customer_price_list_fk
            FOREIGN KEY (id, default_customer_price_list_id) REFERENCES price_lists (organization_id, id);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'price_lists_floor_branch_fk') THEN
        ALTER TABLE price_lists
            ADD CONSTRAINT price_lists_floor_branch_fk
            FOREIGN KEY (branch_id, floor_price_list_id) REFERENCES price_lists (branch_id, id);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'price_lists_floor_not_self_ck') THEN
        ALTER TABLE price_lists
            ADD CONSTRAINT price_lists_floor_not_self_ck
            CHECK (floor_price_list_id IS NULL OR floor_price_list_id <> id);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS customers_price_list_idx ON customers (price_list_id) WHERE price_list_id IS NOT NULL;

COMMIT;
