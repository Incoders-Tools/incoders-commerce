using System.Globalization;
using Commerce.Domain.CurrentAccounts;
using Commerce.Domain.Discounts;
using Commerce.Domain.Sales;
using Commerce.Domain.Sync;
using Commerce.Domain.Sync.Payloads;
using Microsoft.Data.Sqlite;

namespace Commerce.BranchNode;

/// <summary>A payment a customer made at this terminal against its current account (a "cobro"), voided or not.</summary>
public sealed record CustomerPaymentRecord(
    Guid PaymentId,
    Guid CustomerId,
    string? CustomerName,
    decimal Amount,
    SaleTender Tender,
    DateTimeOffset ReceivedAtUtc,
    Guid? CashSessionId,
    Guid OperatorId,
    string? Note,
    SaleVoidRecord? Void);

public enum CustomerPaymentOutcome
{
    Recorded,
    NoOpenCashSession,
    AlreadyRecorded,
}

/// <summary>
/// What this terminal knows of a customer's current account: the balance and overdue amount the cloud sent with the last
/// `price-lists` snapshot (<see cref="AsOfUtc"/>), corrected by what this terminal did since then and the cloud had not
/// counted yet: sales on current account (more debt) and payments received (less debt). Null balance: never synced.
/// </summary>
public sealed record CustomerAccountView(
    decimal? SyncedBalance,
    decimal? SyncedOverdue,
    DateTimeOffset? AsOfUtc,
    decimal PendingSalesOnAccount,
    decimal PendingPayments,
    PaymentTerms Terms)
{
    /// <summary>The best estimate of what the customer owes now.</summary>
    public decimal EstimatedBalance => (SyncedBalance ?? 0m) + PendingSalesOnAccount - PendingPayments;
}

/// <summary>
/// Customer payments received at the POS (<c>customer_payments</c>, voids in <c>customer_payment_voids</c>) and the
/// customer account figures replicated from the cloud (<c>customer_balances_replica</c>, <c>customer_terms_replica</c>).
/// A payment is recorded with its <c>customer-payment.received</c> envelope in one transaction and only while a cash
/// session is open: a cash payment is cash in the drawer. Voiding follows the sale void: a separate record, the open
/// session only, the branch PIN, and its own envelope.
/// </summary>
public sealed partial class BranchSyncStore
{
    /// <summary>The local time the last `price-lists` snapshot was applied (to know what its balances already count).</summary>
    private const string PriceListsAppliedLocallyChannel = "price-lists-applied-local";

    private void EnsureCustomerPaymentStorageExists()
    {
        using (var create = _connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS customer_payments (
                    payment_id TEXT PRIMARY KEY,
                    customer_id TEXT NOT NULL,
                    amount TEXT NOT NULL,
                    tender_method TEXT NOT NULL,
                    tender_amount_received TEXT NULL,
                    tender_change TEXT NULL,
                    cash_session_id TEXT NULL,
                    received_at_utc TEXT NOT NULL,
                    operator_id TEXT NOT NULL,
                    note TEXT NULL,
                    operation_id TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_customer_payments_session ON customer_payments (cash_session_id);
                CREATE INDEX IF NOT EXISTS ix_customer_payments_received ON customer_payments (received_at_utc);
                CREATE TABLE IF NOT EXISTS customer_payment_voids (
                    payment_id TEXT PRIMARY KEY,
                    cash_session_id TEXT NULL,
                    voided_at_utc TEXT NOT NULL,
                    voided_by_operator_id TEXT NOT NULL,
                    auth_method TEXT NOT NULL,
                    auth_operator_id TEXT NOT NULL,
                    auth_pin_version TEXT NOT NULL,
                    reason TEXT NOT NULL,
                    operation_id TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS customer_balances_replica (
                    customer_id TEXT PRIMARY KEY,
                    balance TEXT NOT NULL,
                    overdue TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS customer_terms_replica (
                    customer_id TEXT PRIMARY KEY,
                    days INTEGER NOT NULL
                );
                """;
            create.ExecuteNonQuery();
        }

        EnsureColumns("cash_sessions", "collected_cash", "collected_card", "collected_qr", "collection_count");
        EnsureColumns("price_list_settings", "default_customer_payment_terms_days");
    }

    /// <summary>Records a payment of the open cash session and queues its envelope, in one transaction.</summary>
    public CustomerPaymentOutcome RecordCustomerPayment(CustomerPaymentRecord payment, SyncEnvelope envelope)
    {
        lock (_writeGate)
        {
            using var transaction = _connection.BeginTransaction();
            if (!IsCashSessionOpen(payment.CashSessionId, transaction))
            {
                transaction.Rollback();
                return CustomerPaymentOutcome.NoOpenCashSession;
            }

            using (var exists = _connection.CreateCommand())
            {
                exists.Transaction = transaction;
                exists.CommandText = "SELECT 1 FROM customer_payments WHERE payment_id = $id;";
                exists.Parameters.AddWithValue("$id", payment.PaymentId.ToString());
                if (exists.ExecuteScalar() is not null)
                {
                    transaction.Rollback();
                    return CustomerPaymentOutcome.AlreadyRecorded;
                }
            }

            using (var insert = _connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO customer_payments
                        (payment_id, customer_id, amount, tender_method, tender_amount_received, tender_change, cash_session_id,
                         received_at_utc, operator_id, note, operation_id)
                    VALUES ($id, $customer, $amount, $method, $received, $change, $session, $at, $operator, $note, $operation);
                    """;
                insert.Parameters.AddWithValue("$id", payment.PaymentId.ToString());
                insert.Parameters.AddWithValue("$customer", payment.CustomerId.ToString());
                insert.Parameters.AddWithValue("$amount", payment.Amount.ToString(CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue("$method", payment.Tender.Method);
                insert.Parameters.AddWithValue("$received", DecimalOrNull(payment.Tender.AmountReceived));
                insert.Parameters.AddWithValue("$change", DecimalOrNull(payment.Tender.ChangeGiven));
                insert.Parameters.AddWithValue("$session", (object?)payment.CashSessionId?.ToString() ?? DBNull.Value);
                insert.Parameters.AddWithValue("$at", payment.ReceivedAtUtc.ToUniversalTime().ToString("O"));
                insert.Parameters.AddWithValue("$operator", payment.OperatorId.ToString());
                insert.Parameters.AddWithValue("$note", (object?)payment.Note ?? DBNull.Value);
                insert.Parameters.AddWithValue("$operation", envelope.OperationId.ToString());
                insert.ExecuteNonQuery();
            }

            InsertSyncOutboxRow(envelope, transaction);
            transaction.Commit();
            return CustomerPaymentOutcome.Recorded;
        }
    }

    /// <summary>
    /// Voids a payment of the open cash session (same rules as a sale void): the void and its envelope in one transaction,
    /// nothing written when the payment is unknown, already voided, or of a closed session.
    /// </summary>
    public SaleVoidResult VoidCustomerPayment(
        Guid paymentId,
        Func<CustomerPaymentRecord, SaleVoidRecord> buildVoid,
        Func<CustomerPaymentRecord, SaleVoidRecord, SyncEnvelope> buildEnvelope)
    {
        lock (_writeGate)
        {
            using var transaction = _connection.BeginTransaction();
            if (ReadCustomerPayments("p.payment_id = $id", transaction, ("$id", paymentId.ToString())).FirstOrDefault() is not { } payment)
            {
                transaction.Rollback();
                return new SaleVoidResult(SaleVoidOutcome.NotFound, null);
            }

            if (payment.Void is { } existing)
            {
                transaction.Rollback();
                return new SaleVoidResult(SaleVoidOutcome.AlreadyVoided, existing);
            }

            if (!IsCashSessionOpen(payment.CashSessionId, transaction))
            {
                transaction.Rollback();
                return new SaleVoidResult(SaleVoidOutcome.SessionNotOpen, null);
            }

            var record = buildVoid(payment);
            using (var insert = _connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO customer_payment_voids
                        (payment_id, cash_session_id, voided_at_utc, voided_by_operator_id, auth_method, auth_operator_id,
                         auth_pin_version, reason, operation_id)
                    VALUES ($id, $session, $at, $operator, $method, $authOperator, $pinVersion, $reason, $operation);
                    """;
                insert.Parameters.AddWithValue("$id", paymentId.ToString());
                insert.Parameters.AddWithValue("$session", (object?)record.CashSessionId?.ToString() ?? DBNull.Value);
                insert.Parameters.AddWithValue("$at", record.VoidedAtUtc.ToUniversalTime().ToString("O"));
                insert.Parameters.AddWithValue("$operator", record.VoidedByOperatorId.ToString());
                insert.Parameters.AddWithValue("$method", record.Authorization.Method);
                insert.Parameters.AddWithValue("$authOperator", record.Authorization.OperatorId.ToString());
                insert.Parameters.AddWithValue("$pinVersion", record.Authorization.PinVersion.ToString(CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue("$reason", record.Reason);
                insert.Parameters.AddWithValue("$operation", record.OperationId.ToString());
                insert.ExecuteNonQuery();
            }

            InsertSyncOutboxRow(buildEnvelope(payment, record), transaction);
            transaction.Commit();
            return new SaleVoidResult(SaleVoidOutcome.Voided, record);
        }
    }

    /// <summary>The payments received in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>), newest first.</summary>
    public IReadOnlyList<CustomerPaymentRecord> ListCustomerPayments(DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        ReadCustomerPayments(
            "p.received_at_utc >= $from AND p.received_at_utc < $to", null,
            ("$from", fromUtc.ToUniversalTime().ToString("O")), ("$to", toUtc.ToUniversalTime().ToString("O")));

    private List<CustomerPaymentRecord> ReadCustomerPayments(string where, SqliteTransaction? transaction, params (string Name, object Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT p.payment_id, p.customer_id, c.display_name, p.amount, p.tender_method, p.tender_amount_received, p.tender_change,
                   p.cash_session_id, p.received_at_utc, p.operator_id, p.note,
                   v.cash_session_id, v.voided_at_utc, v.voided_by_operator_id, v.auth_method, v.auth_operator_id,
                   v.auth_pin_version, v.reason, v.operation_id, v.payment_id
            FROM customer_payments p
            LEFT JOIN customers_replica c ON c.customer_id = p.customer_id
            LEFT JOIN customer_payment_voids v ON v.payment_id = p.payment_id
            WHERE {where}
            ORDER BY p.received_at_utc DESC;
            """;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var payments = new List<CustomerPaymentRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var paymentId = Guid.Parse(reader.GetString(0));
            payments.Add(new CustomerPaymentRecord(
                paymentId,
                Guid.Parse(reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                decimal.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                new SaleTender(reader.GetString(4), ReadDecimalOrNull(reader, 5), ReadDecimalOrNull(reader, 6)),
                DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
                reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7)),
                Guid.Parse(reader.GetString(9)),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(19) ? null : ReadSaleVoid(paymentId, reader, 11)));
        }

        return payments;
    }

    /// <summary>The not-voided payments of a session, as its totals need them.</summary>
    private List<CashSessionCollectionRow> ReadSessionCollections(Guid sessionId, SqliteTransaction? transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT amount, tender_method FROM customer_payments
            WHERE cash_session_id = $id AND payment_id NOT IN (SELECT payment_id FROM customer_payment_voids);
            """;
        command.Parameters.AddWithValue("$id", sessionId.ToString());
        var rows = new List<CashSessionCollectionRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new CashSessionCollectionRow(decimal.Parse(reader.GetString(0), CultureInfo.InvariantCulture), reader.GetString(1)));
        }

        return rows;
    }

    private readonly record struct CashSessionCollectionRow(decimal Amount, string Method);

    /// <summary>
    /// What this terminal knows of the customer's account: the last synced balance and terms, plus what it did since the
    /// cloud last counted (operations still pending, or acknowledged after the snapshot was applied).
    /// </summary>
    public CustomerAccountView GetCustomerAccountView(Guid customerId)
    {
        decimal? balance = null, overdue = null;
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT balance, overdue FROM customer_balances_replica WHERE customer_id = $id;";
            command.Parameters.AddWithValue("$id", customerId.ToString());
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                balance = decimal.Parse(reader.GetString(0), CultureInfo.InvariantCulture);
                overdue = decimal.Parse(reader.GetString(1), CultureInfo.InvariantCulture);
            }
        }

        var applied = ReadCursor(PriceListsAppliedLocallyChannel);
        var asOf = GetPriceListsCursor();
        if (asOf is not null && balance is null)
        {
            // Synced, and the cloud sent no balance: the customer owes nothing.
            balance = 0m;
            overdue = 0m;
        }

        var notCounted = """
            (o.status = 'Pending' OR $applied IS NULL OR o.acknowledged_at_utc > $applied)
            """;
        decimal Sum(string sql)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", customerId.ToString());
            command.Parameters.AddWithValue("$applied", (object?)applied?.ToString("O") ?? DBNull.Value);
            using var reader = command.ExecuteReader();
            var total = 0m;
            while (reader.Read())
            {
                total += decimal.Parse(reader.GetString(0), CultureInfo.InvariantCulture);
            }

            return total;
        }

        var pendingSales = Sum($"""
            SELECT s.total_amount FROM sale_effects s
            JOIN sync_outbox o ON o.aggregate_id = s.sale_id AND o.payload_kind = 'sale'
            WHERE s.customer_id = $id AND s.tender_method = '{SaleTender.Account}'
              AND s.sale_id NOT IN (SELECT sale_id FROM sale_voids) AND {notCounted};
            """);
        var pendingPayments = Sum($"""
            SELECT p.amount FROM customer_payments p
            JOIN sync_outbox o ON o.aggregate_id = p.payment_id AND o.payload_kind = '{CustomerPaymentPayloadKinds.Received}'
            WHERE p.customer_id = $id AND p.payment_id NOT IN (SELECT payment_id FROM customer_payment_voids) AND {notCounted};
            """);

        return new CustomerAccountView(balance, overdue, asOf, pendingSales, pendingPayments, GetCustomerPaymentTerms(customerId));
    }

    /// <summary>The customer's payment terms as last synced: its own days, else the organization's default (30 when never synced).</summary>
    public PaymentTerms GetCustomerPaymentTerms(Guid customerId)
    {
        int? days = null;
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT days FROM customer_terms_replica WHERE customer_id = $id;";
            command.Parameters.AddWithValue("$id", customerId.ToString());
            if (command.ExecuteScalar() is long value)
            {
                days = (int)value;
            }
        }

        var organizationDefault = PaymentTerms.DefaultDays;
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT default_customer_payment_terms_days FROM price_list_settings WHERE id = 1;";
            if (command.ExecuteScalar() is string text && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                organizationDefault = parsed;
            }
        }

        return PaymentTerms.For(days, organizationDefault);
    }

    /// <summary>Replaces the replicated balances and terms (part of the `price-lists` snapshot transaction).</summary>
    private void WriteCustomerAccountsSnapshot(
        IReadOnlyList<CustomerBalanceReplica>? balances, IReadOnlyList<CustomerTermsReplica>? terms, int? defaultTermsDays,
        SqliteTransaction transaction)
    {
        Exec(transaction, "DELETE FROM customer_balances_replica; DELETE FROM customer_terms_replica;");
        foreach (var balance in balances ?? [])
        {
            Exec(transaction,
                "INSERT INTO customer_balances_replica (customer_id, balance, overdue) VALUES ($customer, $balance, $overdue);",
                ("$customer", balance.CustomerId.ToString()), ("$balance", balance.Balance.ToString(CultureInfo.InvariantCulture)),
                ("$overdue", balance.Overdue.ToString(CultureInfo.InvariantCulture)));
        }

        foreach (var term in terms ?? [])
        {
            Exec(transaction,
                "INSERT INTO customer_terms_replica (customer_id, days) VALUES ($customer, $days);",
                ("$customer", term.CustomerId.ToString()), ("$days", term.Days));
        }

        Exec(transaction,
            "UPDATE price_list_settings SET default_customer_payment_terms_days = $days WHERE id = 1;",
            ("$days", defaultTermsDays?.ToString(CultureInfo.InvariantCulture)));
        UpsertCursor(PriceListsAppliedLocallyChannel, DateTimeOffset.UtcNow, transaction);
    }

    private DateTimeOffset? ReadCursor(string channel)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT last_synced_utc FROM sync_cursors WHERE channel = $channel;";
        command.Parameters.AddWithValue("$channel", channel);
        return command.ExecuteScalar() is string raw ? DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture) : null;
    }
}

/// <summary>The outcome of receiving a customer payment; <see cref="Payment"/> is set when it was recorded.</summary>
public sealed record CustomerPaymentResult(CustomerPaymentOutcome Outcome, CustomerPaymentRecord? Payment);
