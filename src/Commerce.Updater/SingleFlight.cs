namespace Commerce.Updater;

/// <summary>
/// Runs one operation at a time. A caller that arrives while a run is in
/// flight awaits that same run instead of being dropped or starting a second
/// one, so a manual "check for updates" during the startup check shows the
/// result of the check that is already running.
/// </summary>
public sealed class SingleFlight
{
    private readonly object _gate = new();
    private Task? _current;

    public Task RunAsync(Func<Task> work)
    {
        lock (_gate)
        {
            if (_current is { IsCompleted: false } running)
            {
                return running;
            }

            return _current = StartAsync(work);
        }
    }

    // Task.Run-free on purpose: the work starts on the caller's context (the UI
    // thread for the POS) and only its own awaits move it elsewhere.
    private static async Task StartAsync(Func<Task> work) => await work();
}
