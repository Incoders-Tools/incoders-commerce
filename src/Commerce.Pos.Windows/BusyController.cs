namespace Commerce.Pos.Windows;

/// <summary>
/// Serializes the network actions of one window: while <see cref="RunAsync"/>
/// is running the window renders its busy state (inputs locked, progress bar
/// visible), a second action is ignored instead of firing a duplicate request,
/// an exception is logged and shown as the friendly message instead of
/// escaping an <c>async void</c> handler, and the busy state is always
/// restored. UI-free on purpose: the window supplies the two callbacks.
/// </summary>
public sealed class BusyController
{
    private readonly Action<bool, string?> _render;
    private readonly string _category;
    private readonly Action<string> _showError;

    public BusyController(Action<bool, string?> render, string category, Action<string> showError)
    {
        _render = render;
        _category = category;
        _showError = showError;
    }

    private readonly CancellationTokenSource _cancellation = new();

    public bool IsBusy { get; private set; }

    /// <summary>Cancelled by <see cref="Cancel"/>; pass it to the requests so a torn-down window stops waiting on them.</summary>
    public CancellationToken Token => _cancellation.Token;

    /// <summary>Cancels the work in flight (the window is going away). The work still ends through the normal finally path.</summary>
    public void Cancel() => _cancellation.Cancel();

    /// <summary>Raised once the work ended and the busy state was restored (never for an ignored re-entrant call).</summary>
    public event Action? Idle;

    public async Task RunAsync(string busyText, Func<Task> work)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        _render(true, busyText);
        try
        {
            await work();
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            // The window was torn down under the request: nobody is looking at the result.
        }
        catch (Exception ex)
        {
            PosLog.Error(_category, "Unhandled error while running a window action.", ex);
            _showError(PosMessages.Unexpected);
        }
        finally
        {
            IsBusy = false;
            try
            {
                _render(false, null);
            }
            finally
            {
                // Always raised, even when rendering failed: the shell waits for it to release a detached section.
                Idle?.Invoke();
            }
        }
    }
}
