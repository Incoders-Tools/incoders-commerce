using System.Globalization;
using Commerce.Domain.Discounts;
using Commerce.Domain.Sales;
using Commerce.Domain.Sync;
using Microsoft.Data.Sqlite;

namespace Commerce.BranchNode;

/// <summary>
/// The branch discount PIN verifier as cached on a terminal (branch-discount-pin
/// spec): replicated from the cloud, never containing the PIN, and enough to
/// verify a typed PIN with no network.
/// </summary>
public sealed record DiscountPinReplica(Guid BranchId, long Version, DiscountPinVerifier Verifier, DateTimeOffset ChangedAtUtc);

/// <summary>
/// Discount storage of the branch database: the cached PIN verifier, the
/// per-terminal lockout state, and the columns that record discounts on sales.
/// Kept apart from the main store file; every change here is additive and
/// idempotent so a `branch.db` from before discounts opens and upgrades.
/// </summary>
public sealed partial class BranchSyncStore
{
    private void EnsureDiscountStorageExists()
    {
        using (var create = _connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS discount_pin_replica (
                    branch_id TEXT PRIMARY KEY,
                    version INTEGER NOT NULL,
                    algorithm TEXT NOT NULL,
                    iterations INTEGER NOT NULL,
                    salt TEXT NOT NULL,
                    pin_hash TEXT NOT NULL,
                    changed_at_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS discount_pin_lockout (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    failed_attempts INTEGER NOT NULL,
                    locked_until_utc TEXT NULL
                );
                """;
            create.ExecuteNonQuery();
        }

        EnsureColumns("sale_effects",
            "sale_discount_percent", "sale_discount_amount", "discount_auth_method", "discount_operator_id", "discount_pin_version");
        EnsureColumns("sale_lines", "line_discount_percent", "line_discount_amount");
    }

    /// <summary>Adds each nullable TEXT column only when missing (SQLite has no ADD COLUMN IF NOT EXISTS).</summary>
    private void EnsureColumns(string table, params string[] columns)
    {
        var existing = new HashSet<string>(StringComparer.Ordinal);
        using (var check = _connection.CreateCommand())
        {
            check.CommandText = $"PRAGMA table_info({table});";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                existing.Add(reader.GetString(1));
            }
        }

        foreach (var column in columns.Where(c => !existing.Contains(c)))
        {
            using var alter = _connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} TEXT NULL;";
            alter.ExecuteNonQuery();
        }
    }

    private static object DecimalOrNull(decimal? value) =>
        value is { } v ? v.ToString(CultureInfo.InvariantCulture) : DBNull.Value;

    private static decimal? ReadDecimalOrNull(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : decimal.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);

    /// <summary>The stored effect of a sale (null when unknown), including its discounts and authorization.</summary>
    public SaleEffect? GetSaleEffect(Guid saleId)
    {
        using var find = _connection.CreateCommand();
        find.CommandText = "SELECT branch_id FROM sale_effects WHERE sale_id = $saleId;";
        find.Parameters.AddWithValue("$saleId", saleId.ToString());
        return find.ExecuteScalar() is string branchId ? ReadSaleEffect(saleId, Guid.Parse(branchId), transaction: null) : null;
    }

    private SaleEffect? ReadSaleEffect(Guid saleId, Guid branchId, SqliteTransaction? transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT total_amount, occurred_at_utc, sale_kind, customer_id,
                   sale_discount_percent, sale_discount_amount,
                   discount_auth_method, discount_operator_id, discount_pin_version,
                   tender_method, tender_amount_received, tender_change, cash_session_id
            FROM sale_effects WHERE sale_id = $saleId;
            """;
        command.Parameters.AddWithValue("$saleId", saleId.ToString());
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var authorization = reader.IsDBNull(6)
            ? null
            : new DiscountAuthorization(
                reader.GetString(6),
                Guid.Parse(reader.GetString(7)),
                long.Parse(reader.GetString(8), CultureInfo.InvariantCulture));

        return new SaleEffect(
            saleId,
            branchId,
            decimal.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
            ReadDecimalOrNull(reader, 4),
            ReadDecimalOrNull(reader, 5),
            authorization,
            reader.IsDBNull(9) ? null : new SaleTender(reader.GetString(9), ReadDecimalOrNull(reader, 10), ReadDecimalOrNull(reader, 11)),
            reader.IsDBNull(12) ? null : Guid.Parse(reader.GetString(12)));
    }

    // ---- branch discount PIN replica ----

    /// <summary>Caches the branch verifier, replacing any previous one; <c>null</c> means the branch has none.</summary>
    public void ApplyDiscountPin(Guid branchId, DiscountPinReplica? replica)
    {
        lock (_writeGate)
        {
            using var transaction = _connection.BeginTransaction();

            using (var delete = _connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM discount_pin_replica WHERE branch_id = $branchId;";
                delete.Parameters.AddWithValue("$branchId", branchId.ToString());
                delete.ExecuteNonQuery();
            }

            if (replica is not null)
            {
                using var insert = _connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO discount_pin_replica (branch_id, version, algorithm, iterations, salt, pin_hash, changed_at_utc)
                    VALUES ($branchId, $version, $algorithm, $iterations, $salt, $hash, $changedAt);
                    """;
                insert.Parameters.AddWithValue("$branchId", branchId.ToString());
                insert.Parameters.AddWithValue("$version", replica.Version);
                insert.Parameters.AddWithValue("$algorithm", replica.Verifier.Algorithm);
                insert.Parameters.AddWithValue("$iterations", replica.Verifier.Iterations);
                insert.Parameters.AddWithValue("$salt", Convert.ToBase64String(replica.Verifier.Salt));
                insert.Parameters.AddWithValue("$hash", Convert.ToBase64String(replica.Verifier.Hash));
                insert.Parameters.AddWithValue("$changedAt", replica.ChangedAtUtc.ToString("O"));
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    /// <summary>The cached verifier of <paramref name="branchId"/>, or null when none is cached for that branch.</summary>
    public DiscountPinReplica? GetDiscountPin(Guid branchId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT version, algorithm, iterations, salt, pin_hash, changed_at_utc
            FROM discount_pin_replica WHERE branch_id = $branchId;
            """;
        command.Parameters.AddWithValue("$branchId", branchId.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new DiscountPinReplica(
                branchId,
                reader.GetInt64(0),
                new DiscountPinVerifier(
                    reader.GetString(1), reader.GetInt32(2),
                    Convert.FromBase64String(reader.GetString(3)), Convert.FromBase64String(reader.GetString(4))),
                DateTimeOffset.Parse(reader.GetString(5)))
            : null;
    }

    // ---- per-terminal PIN lockout ----

    public PinLockoutState GetDiscountPinLockout()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT failed_attempts, locked_until_utc FROM discount_pin_lockout WHERE id = 1;";
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new PinLockoutState(reader.GetInt32(0), reader.IsDBNull(1) ? null : DateTimeOffset.Parse(reader.GetString(1)))
            : PinLockoutState.None;
    }

    public void SetDiscountPinLockout(PinLockoutState state)
    {
        lock (_writeGate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO discount_pin_lockout (id, failed_attempts, locked_until_utc)
                VALUES (1, $failed, $lockedUntil)
                ON CONFLICT(id) DO UPDATE SET
                    failed_attempts = excluded.failed_attempts,
                    locked_until_utc = excluded.locked_until_utc;
                """;
            command.Parameters.AddWithValue("$failed", state.FailedAttempts);
            command.Parameters.AddWithValue("$lockedUntil", state.LockedUntilUtc is { } until ? until.ToString("O") : DBNull.Value);
            command.ExecuteNonQuery();
        }
    }
}
