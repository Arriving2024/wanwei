namespace Wanwei;

internal enum NativeConnectionOperationState { Started, Completed, Canceled, Error, Unknown }

// Dispatcher treats OperationCanceledException as cancellation and can replace
// its concrete type with TaskCanceledException. Keep expiry distinct from the
// user's cancellation token across the actual STA handoff.
internal sealed class NativeConnectionBoundaryException() : Exception("The original native connection boundary has ended.");

// This deliberately contains no discovery, pairing or identity lookup. The
// selected request ID is immutable across the one possible retry.
internal interface INativeConnectionOperation<T> where T : class
{
    Task<T?> Completion { get; }
    NativeConnectionOperationState State { get; }
    void Cancel();
    void Close();
}

internal static class NativeConnectionRetry
{
    internal const int EnumerationError = unchecked((int)0x80070288);
    internal const int BackoffMs = 250;

    internal static async Task<T?> RunAsync<T>(string selectedRequestId,
        Func<string, CancellationToken, Task<INativeConnectionOperation<T>>> beginOnUi,
        bool existingSystemAssociation, bool currentRequestAccepted,
        Func<bool> mayContinue, Action<string> record, Action<object, string> retainUncertain,
        Action<T> releaseLate, CancellationToken cancellation,
        Func<int, CancellationToken, Task>? delay = null, TimeSpan? cancellationSettlement = null) where T : class
    {
        delay ??= static (milliseconds, token) => Task.Delay(milliseconds, token);
        TimeSpan settlement = cancellationSettlement ?? TimeSpan.FromSeconds(5);
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!mayContinue()) return null;
            // Do not cancel the UI handoff with WaitAsync: once begun, the native
            // operation must always reach the late-result owner below.
            INativeConnectionOperation<T> operation;
            try { operation = await beginOnUi(selectedRequestId, cancellation).ConfigureAwait(false); }
            catch (Exception ex)
            {
                record($"attempt_{attempt}_begin_failed:{Code(ex)}");
                throw;
            }
            Task<T?> completion;
            try { completion = operation.Completion; }
            catch (Exception ex)
            {
                try { operation.Cancel(); } catch { }
                retainUncertain(operation, "NATIVE_CONNECTION_PROJECTION_UNCONFIRMED");
                record($"attempt_{attempt}_projection_failed:{Code(ex)}");
                throw;
            }
            T? value;
            try
            {
                value = await ObserveAsync(operation, completion, releaseLate, retainUncertain, cancellation, settlement).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                record($"attempt_{attempt}_cancelled");
                throw;
            }
            catch (Exception ex)
            {
                record($"attempt_{attempt}_failed:{Code(ex)}");
                bool errorTerminated;
                try { errorTerminated = operation.State == NativeConnectionOperationState.Error; }
                catch { errorTerminated = false; }
                if (!errorTerminated)
                {
                    // A failed projection is not evidence that the native
                    // operation has ended. Never start another operation here.
                    try { operation.Cancel(); } catch { }
                    retainUncertain(operation, "NATIVE_CONNECTION_TERMINATION_UNCONFIRMED");
                    record($"attempt_{attempt}_native_error_terminal_unconfirmed");
                    throw;
                }
                try
                {
                    operation.Close();
                    record($"attempt_{attempt}_failed_operation_closed");
                }
                catch
                {
                    retainUncertain(operation, "NATIVE_CONNECTION_OPERATION_CLOSE_UNCONFIRMED");
                    record($"attempt_{attempt}_failed_operation_close_unconfirmed");
                    throw;
                }
                if (attempt != 1 || ex.HResult != EnumerationError ||
                    !existingSystemAssociation || !currentRequestAccepted) throw;
                cancellation.ThrowIfCancellationRequested();
                if (!mayContinue()) return null;
                record("same_request_enumeration_retry_backoff_250ms");
                await delay(BackoffMs, cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                if (!mayContinue()) return null;
                record("same_request_enumeration_retry_started");
                continue;
            }
            // WaitAsync can return an already completed result despite token
            // cancellation. A deadline can also expire during the UI handoff.
            if (value is not null && (cancellation.IsCancellationRequested || !mayContinue()))
            {
                bool lateReleased = ReleaseLate(value, operation, releaseLate, retainUncertain);
                record($"attempt_{attempt}_late_device_" + (lateReleased ? "released" : "release_unconfirmed"));
                cancellation.ThrowIfCancellationRequested();
                return null;
            }
            return value; // Null is a terminal failure, never a reason to retry.
        }
        throw new InvalidOperationException("Native connection retry bound exceeded.");
    }

    private static string Code(Exception exception) => $"{exception.GetType().Name}:0x{exception.HResult:X8}";

    private static bool ReleaseLate<T>(T value, object operation, Action<T> releaseLate,
        Action<object, string> retainUncertain) where T : class
    {
        try { releaseLate(value); return true; }
        catch { retainUncertain(operation, "NATIVE_CONNECTION_LATE_RELEASE_UNCONFIRMED"); return false; }
    }

    private static async Task<T?> ObserveAsync<T>(INativeConnectionOperation<T> operation, Task<T?> task, Action<T> releaseLate,
        Action<object, string> retainUncertain, CancellationToken cancellation, TimeSpan settlement) where T : class
    {
        try { return await task.WaitAsync(cancellation).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            try { operation.Cancel(); }
            catch { retainUncertain(operation, "NATIVE_CONNECTION_CANCEL_UNCONFIRMED"); }
            if (await Task.WhenAny(task, Task.Delay(settlement)).ConfigureAwait(false) == task)
            {
                try
                {
                    T? late = await task.ConfigureAwait(false);
                    if (late is not null) ReleaseLate(late, operation, releaseLate, retainUncertain);
                }
                catch (OperationCanceledException) { }
                catch (Exception) when (task.IsFaulted) { }
            }
            else
            {
                retainUncertain(operation, "NATIVE_CONNECTION_CANCEL_UNCONFIRMED");
                _ = task.ContinueWith(t => {
                    if (t.IsCompletedSuccessfully && t.Result is not null)
                        ReleaseLate(t.Result, operation, releaseLate, retainUncertain);
                    else _ = t.Exception;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            throw;
        }
    }
}
