namespace Commerce.Pos.Windows;

/// <summary>
/// The Clientes form's Guardar on an existing customer (operator-ux-adjustments T7). UI-free. The update carries the
/// <c>UpdatedAtUtc</c> of the record the form was filled from, as the web form does, so the server refuses it (409
/// <c>customer-modified</c>) when someone saved the customer meanwhile; then the customer is read again so the form can
/// show it as stored now and the operator re-applies the changes. Creating a customer is unchanged.
/// </summary>
public static class CustomerFormSave
{
    /// <summary>
    /// Sends the update with <paramref name="loadedUpdatedAtUtc"/>. <see cref="CustomerFormSaveResult.Current"/> is the
    /// customer as saved (the version the still open form carries next), or, after a 409 <c>customer-modified</c>, as
    /// read again (null when it could not be read: the form stays as it is).
    /// </summary>
    public static async Task<CustomerFormSaveResult> UpdateAsync(
        CustomerAdminClient client, Guid id, DateTimeOffset loadedUpdatedAtUtc, UpdateCustomerAdminRequestDto request,
        CancellationToken ct = default)
    {
        var outcome = await client.UpdateCustomerAsync(id, request with { ExpectedUpdatedAtUtc = loadedUpdatedAtUtc }, ct);
        if (outcome.Kind != CustomerAdminMutationKind.Modified)
        {
            return new CustomerFormSaveResult(outcome, outcome.Customer);
        }

        var read = await client.GetCustomerAsync(id, ct);
        return read.Customer is { } current
            ? new CustomerFormSaveResult(CustomerAdminMutationOutcome.Modified(PosMessages.CustomerReloadedAfterConflict), current)
            : new CustomerFormSaveResult(outcome, null);
    }

    /// <summary>
    /// The version the open form carries after Habilitar / Deshabilitar on the same customer: the toggle's saved one
    /// when the form was filled from the row the toggle acted on, so the form's next Guardar is not refused for the
    /// operator's own toggle; otherwise the form's own, so a change it has not seen still makes the server refuse it.
    /// </summary>
    public static DateTimeOffset VersionAfterToggle(DateTimeOffset loaded, CustomerAdminRecordDto row, CustomerAdminRecordDto? saved) =>
        saved is not null && row.UpdatedAtUtc == loaded ? saved.UpdatedAtUtc : loaded;
}

/// <summary>The outcome of <see cref="CustomerFormSave.UpdateAsync"/> and the customer the form shows next, if any.</summary>
public sealed record CustomerFormSaveResult(CustomerAdminMutationOutcome Outcome, CustomerAdminRecordDto? Current);
