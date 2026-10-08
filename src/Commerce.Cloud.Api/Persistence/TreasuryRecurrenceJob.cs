using Commerce.Application.Time;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Records the recurring treasury movements due by today for every organization, at start and every few hours, so a
/// fixed expense lands on its date even when nobody opens the treasury. The list of organizations comes from the
/// platform-read connection (the only cross-organization read); each organization is then processed under its own
/// tenant scope like any request. Without that connection the job does nothing: the treasury still records what is
/// due whenever it is read. Idempotent: a date already recorded is never recorded again.
/// </summary>
public sealed class TreasuryRecurrenceJob : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromHours(6);

    private readonly PostgresOrganizationStore _organizations;
    private readonly PostgresTreasuryRecurrenceStore _recurrences;
    private readonly IBusinessClock _clock;
    private readonly ILogger<TreasuryRecurrenceJob> _logger;

    public TreasuryRecurrenceJob(
        PostgresOrganizationStore organizations, PostgresTreasuryRecurrenceStore recurrences, IBusinessClock clock,
        ILogger<TreasuryRecurrenceJob> logger)
    {
        _organizations = organizations;
        _recurrences = recurrences;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_organizations.CanListOrganizations)
        {
            _logger.LogInformation("Recurring treasury movements: no platform-read connection; recorded when the treasury is read.");
            return;
        }

        using var timer = new PeriodicTimer(Period);
        do
        {
            await RunOnceAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One pass over every organization; a failing one is logged and the others still run.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<Guid> organizationIds;
        try
        {
            organizationIds = [.. (await _organizations.ListOrganizationsAsync(ct)).Select(organization => organization.Id)];
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Recurring treasury movements: the organizations could not be listed.");
            return;
        }

        var today = _clock.Today;
        foreach (var organizationId in organizationIds)
        {
            try
            {
                var added = await _recurrences.GenerateDueForOrganizationAsync(organizationId, today, ct);
                if (added > 0)
                {
                    _logger.LogInformation("Recorded {Count} recurring treasury movements for {OrganizationId}.", added, organizationId);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Recurring treasury movements of {OrganizationId} were not recorded.", organizationId);
            }
        }
    }
}
