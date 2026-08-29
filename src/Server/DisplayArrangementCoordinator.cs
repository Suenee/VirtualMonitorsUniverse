using System.Text.Json;
using VirtualMonitorsUniverse.Core;

namespace VirtualMonitorsUniverse.Server;

internal sealed record ArrangementApplyResult(DateTime ExpiresAtUtc, int ConfirmSeconds);

/// <summary>
/// Owns the temporary "keep these display settings" transaction used by the web
/// arrangement editor. The rollback timer lives on the server so a lost browser
/// connection cannot strand the host in an unusable topology.
/// </summary>
internal sealed class DisplayArrangementCoordinator : IDisposable
{
    private static readonly TimeSpan ConfirmationWindow = TimeSpan.FromSeconds(15);
    private readonly WindowsDisplayArrangementService _service = new();
    private readonly LogStore _logStore;
    private readonly object _gate = new();
    private PendingArrangement? _pending;
    private bool _disposed;

    public DisplayArrangementCoordinator(LogStore logStore) => _logStore = logStore;

    public ArrangementApplyResult Apply(IReadOnlyCollection<DisplayArrangementEntry> requested)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            RevertPendingLocked("A newer arrangement replaced the unconfirmed display change.");

            var original = _service.CaptureActive();
            _service.Apply(requested);

            var cancellation = new CancellationTokenSource();
            var expiresAt = DateTime.UtcNow.Add(ConfirmationWindow);
            _pending = new PendingArrangement(original, cancellation, expiresAt);
            _ = RollbackAfterTimeoutAsync(_pending);

            _logStore.Write("INFO", "VMU", "ARRANGEMENT_APPLIED_PENDING", "Display arrangement was applied and is awaiting confirmation.", detailsJson: JsonSerializer.Serialize(new
            {
                expiresAtUtc = expiresAt,
                confirmSeconds = (int)ConfirmationWindow.TotalSeconds,
                requested
            }));

            return new ArrangementApplyResult(expiresAt, (int)ConfirmationWindow.TotalSeconds);
        }
    }

    public bool Keep()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_pending is null) return false;
            _pending.Cancellation.Cancel();
            _pending.Cancellation.Dispose();
            _pending = null;
            _logStore.Write("INFO", "VMU", "ARRANGEMENT_CONFIRMED", "Display arrangement was confirmed by the user.");
            return true;
        }
    }

    public bool Revert()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return RevertPendingLocked("Display arrangement was reverted by the user.");
        }
    }

    private async Task RollbackAfterTimeoutAsync(PendingArrangement pending)
    {
        try
        {
            await Task.Delay(ConfirmationWindow, pending.Cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_gate)
        {
            if (!ReferenceEquals(_pending, pending)) return;
            try
            {
                _service.Apply(pending.Original);
                _logStore.Write("WARN", "VMU", "ARRANGEMENT_AUTO_REVERT", "Display arrangement was automatically reverted because it was not confirmed in time.");
            }
            catch (Exception ex)
            {
                _logStore.Write("ERROR", "VMU", "ARRANGEMENT_AUTO_REVERT_FAILED", $"Automatic arrangement rollback failed: {ex.Message}", detailsJson: JsonSerializer.Serialize(new { exception = ex.ToString() }));
            }
            finally
            {
                pending.Cancellation.Dispose();
                _pending = null;
            }
        }
    }

    private bool RevertPendingLocked(string message)
    {
        if (_pending is null) return false;
        var pending = _pending;
        pending.Cancellation.Cancel();
        try
        {
            _service.Apply(pending.Original);
            _logStore.Write("INFO", "VMU", "ARRANGEMENT_REVERTED", message);
        }
        finally
        {
            pending.Cancellation.Dispose();
            _pending = null;
        }
        return true;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_pending is not null)
            {
                try { RevertPendingLocked("Display arrangement was reverted because the web service stopped."); }
                catch { }
            }
            _disposed = true;
        }
    }

    private sealed record PendingArrangement(IReadOnlyList<DisplayArrangementEntry> Original, CancellationTokenSource Cancellation, DateTime ExpiresAtUtc);
}
