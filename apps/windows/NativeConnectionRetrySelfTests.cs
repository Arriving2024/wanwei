namespace Wanwei;

internal static class NativeConnectionRetrySelfTests
{
    internal static void Run(Action<string, bool> check) => RunAsync(check).GetAwaiter().GetResult();

    private static async Task RunAsync(Action<string, bool> check)
    {
        var secondDevice = new Device();
        var firstError = Error();
        var success = new Harness(firstError, Success(secondDevice));
        check("native retry second attempt succeeds after one closed enumeration error", ReferenceEquals(await success.RunAsync(), secondDevice) &&
            success.Begins == 2 && firstError.CloseCalls == 1 && success.Delays.SequenceEqual([250]) && success.Uncertain.Count == 0);
        check("native retry fixes the same selected request ID and preserves first failure events", success.Ids.Count == 2 &&
            success.Ids.All(id => id == Harness.SelectedId) && secondDevice.Releases == 0 &&
            success.Events.Any(e => e.StartsWith("attempt_1_failed:", StringComparison.Ordinal)) &&
            success.Events.Contains("same_request_enumeration_retry_started") &&
            success.Events.All(e => !e.Contains(Harness.SelectedId, StringComparison.Ordinal)));

        var first = Error(); var second = Error(); var twoErrors = new Harness(first, second);
        var finalError = await CaptureAsync(() => twoErrors.RunAsync());
        check("native retry stops after two enumeration failures and closes both error operations", finalError?.HResult == NativeConnectionRetry.EnumerationError &&
            twoErrors.Begins == 2 && first.CloseCalls == 1 && second.CloseCalls == 1 && twoErrors.Delays.Count == 1 &&
            twoErrors.Events.Any(e => e.StartsWith("attempt_1_failed:", StringComparison.Ordinal)) &&
            twoErrors.Events.Any(e => e.StartsWith("attempt_2_failed:", StringComparison.Ordinal)));

        var other = new Harness(Error(unchecked((int)0x80070005)));
        check("native retry refuses other HRESULTs", (await CaptureAsync(() => other.RunAsync()))?.HResult == unchecked((int)0x80070005) &&
            other.Begins == 1 && other.Delays.Count == 0 && other.Operations[0].CloseCalls == 1);
        var fresh = new Harness(Error()) { ExistingAssociation = false };
        check("native retry refuses fresh pairing branch", await CaptureAsync(() => fresh.RunAsync()) is not null && fresh.Begins == 1 && fresh.Delays.Count == 0);
        var unaccepted = new Harness(Error()) { Accepted = false };
        check("native retry requires acceptance of the current request", await CaptureAsync(() => unaccepted.RunAsync()) is not null && unaccepted.Begins == 1 && unaccepted.Delays.Count == 0);
        var nullResult = new Harness(Success(null));
        check("native retry never retries a null native result", await nullResult.RunAsync() is null && nullResult.Begins == 1 && nullResult.Delays.Count == 0);

        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel(); var preCancelled = new Harness(Error());
            check("native retry pre-cancellation starts no native operation", await CaptureAsync(() => preCancelled.RunAsync(cancellation.Token)) is OperationCanceledException && preCancelled.Begins == 0);
        }
        using (var cancellation = new CancellationTokenSource())
        {
            var cancelledBackoff = new Harness(Error()) { DuringDelay = () => cancellation.Cancel() };
            check("native retry cancellation during backoff prevents the second operation", await CaptureAsync(() => cancelledBackoff.RunAsync(cancellation.Token)) is OperationCanceledException &&
                cancelledBackoff.Begins == 1 && cancelledBackoff.Operations[0].CloseCalls == 1 && cancelledBackoff.Delays.SequenceEqual([250]));
        }
        var expiredBackoff = new Harness(Error()); expiredBackoff.DuringDelay = () => expiredBackoff.InBudget = false;
        check("native retry cannot cross the original deadline during backoff", await expiredBackoff.RunAsync() is null && expiredBackoff.Begins == 1 && expiredBackoff.Delays.SequenceEqual([250]));
        var expired = new Harness(Error()) { InBudget = false };
        check("native retry expired original deadline starts no native operation", await expired.RunAsync() is null && expired.Begins == 0);
        var beforeDelay = new Harness(Error()); beforeDelay.Operations[0].DuringClose = () => beforeDelay.InBudget = false;
        check("native retry rechecks the original deadline before even starting backoff", await beforeDelay.RunAsync() is null && beforeDelay.Begins == 1 && beforeDelay.Delays.Count == 0);

        foreach (var state in new[] { NativeConnectionOperationState.Started, NativeConnectionOperationState.Unknown, NativeConnectionOperationState.Canceled, NativeConnectionOperationState.Completed })
        {
            var notError = Error(); notError.CurrentState = state; var nonterminal = new Harness(notError);
            check("native retry requires native Error status rather than projected failure " + state,
                await CaptureAsync(() => nonterminal.RunAsync()) is not null && nonterminal.Begins == 1 &&
                nonterminal.Delays.Count == 0 && notError.CloseCalls == 0 && notError.CancelCalls == 1 &&
                nonterminal.Uncertain.Contains("NATIVE_CONNECTION_TERMINATION_UNCONFIRMED"));
        }
        var unknownState = Error(); unknownState.ThrowState = true; var unknown = new Harness(unknownState);
        check("native retry status query failure is retained and never retried", await CaptureAsync(() => unknown.RunAsync()) is not null &&
            unknown.Begins == 1 && unknown.Delays.Count == 0 && unknown.Uncertain.Count == 1);
        var failedClose = Error(); failedClose.ThrowClose = true; var unclosed = new Harness(failedClose);
        check("native retry error-operation close failure blocks reuse and retains ownership", await CaptureAsync(() => unclosed.RunAsync()) is not null &&
            unclosed.Begins == 1 && unclosed.Delays.Count == 0 && failedClose.CloseCalls == 1 &&
            unclosed.Uncertain.Contains("NATIVE_CONNECTION_OPERATION_CLOSE_UNCONFIRMED") &&
            unclosed.Events.Contains("attempt_1_failed_operation_close_unconfirmed"));
        var projection = Error(); projection.ThrowProjection = true; var failedProjection = new Harness(projection);
        check("native retry projection setup failure cannot masquerade as terminal enumeration error", await CaptureAsync(() => failedProjection.RunAsync()) is not null &&
            failedProjection.Begins == 1 && failedProjection.Delays.Count == 0 && projection.CancelCalls == 1 && projection.CloseCalls == 0 &&
            failedProjection.Uncertain.Contains("NATIVE_CONNECTION_PROJECTION_UNCONFIRMED"));
        var beginError = new Harness(Error()) { ThrowBegin = true };
        check("native retry begin failure without a returned native operation is never retried", await CaptureAsync(() => beginError.RunAsync()) is not null &&
            beginError.Begins == 1 && beginError.Delays.Count == 0);
        var blockedBegin = new Harness(Success(new Device())) { BoundaryBlockedAt = 1 };
        check("native retry preserves boundary expiry separately from user cancellation inside the UI handoff", await CaptureAsync(() => blockedBegin.RunAsync()) is NativeConnectionBoundaryException &&
            blockedBegin.Begins == 1 && blockedBegin.Operations[0].CloseCalls == 0 && blockedBegin.Delays.Count == 0 && blockedBegin.Uncertain.Count == 0);
        var blockedRetry = new Harness(Error(), Success(new Device())) { BoundaryBlockedAt = 2 };
        check("native retry cannot bypass the actual UI handoff boundary on its second attempt", await CaptureAsync(() => blockedRetry.RunAsync()) is NativeConnectionBoundaryException &&
            blockedRetry.Begins == 2 && blockedRetry.Delays.SequenceEqual([250]) && blockedRetry.Operations[0].CloseCalls == 1 && blockedRetry.Operations[1].CloseCalls == 0 &&
            blockedRetry.Events.Any(e => e.StartsWith("attempt_1_failed:", StringComparison.Ordinal)) && blockedRetry.Events.Any(e => e.StartsWith("attempt_2_begin_failed:", StringComparison.Ordinal)));

        var lateDeadlineDevice = new Device(); var lateDeadlineOperation = Success(lateDeadlineDevice);
        var lateDeadline = new Harness(lateDeadlineOperation); lateDeadlineOperation.DuringProjection = () => lateDeadline.InBudget = false;
        check("native retry releases a completed device after the original deadline", await lateDeadline.RunAsync() is null &&
            lateDeadlineDevice.Releases == 1 && lateDeadline.Begins == 1 && lateDeadline.Events.Contains("attempt_1_late_device_released"));
        using (var cancellation = new CancellationTokenSource())
        {
            var lateDevice = new Device(); var lateOperation = Success(lateDevice); lateOperation.DuringProjection = () => cancellation.Cancel();
            var lateCancel = new Harness(lateOperation);
            check("native retry releases an already completed device despite simultaneous cancellation", await CaptureAsync(() => lateCancel.RunAsync(cancellation.Token)) is OperationCanceledException &&
                lateDevice.Releases == 1 && lateCancel.Begins == 1);
        }
        using (var cancellation = new CancellationTokenSource())
        {
            var source = new TaskCompletionSource<Device?>(TaskCreationOptions.RunContinuationsAsynchronously); var lateDevice = new Device();
            var pending = new Operation(source.Task, NativeConnectionOperationState.Started) {
                DuringProjection = () => cancellation.Cancel(), DuringCancel = () => source.TrySetResult(lateDevice)
            };
            var settled = new Harness(pending);
            check("native retry cancellation settlement releases the late native result exactly once", await CaptureAsync(() => settled.RunAsync(cancellation.Token)) is OperationCanceledException &&
                pending.CancelCalls == 1 && settled.Begins == 1 && lateDevice.Releases == 1 && settled.Uncertain.Count == 0);
        }
        using (var cancellation = new CancellationTokenSource())
        {
            var source = new TaskCompletionSource<Device?>(TaskCreationOptions.RunContinuationsAsynchronously); var lateDevice = new Device();
            var pending = new Operation(source.Task, NativeConnectionOperationState.Started) { DuringProjection = () => cancellation.Cancel() };
            var unsettled = new Harness(pending);
            var cancelError = await CaptureAsync(() => unsettled.RunAsync(cancellation.Token));
            source.SetResult(lateDevice);
            await lateDevice.Released.Task.WaitAsync(TimeSpan.FromSeconds(2));
            check("native retry retains unresolved cancellation and releases a result arriving after return", cancelError is OperationCanceledException &&
                unsettled.Begins == 1 && unsettled.Uncertain.Contains("NATIVE_CONNECTION_CANCEL_UNCONFIRMED") && lateDevice.Releases == 1);
        }
        var releaseDevice = new Device { ThrowRelease = true }; var releaseOperation = Success(releaseDevice);
        var failedRelease = new Harness(releaseOperation); releaseOperation.DuringProjection = () => failedRelease.InBudget = false;
        check("native retry late result disposal failure remains resource-release uncertainty", await failedRelease.RunAsync() is null &&
            failedRelease.Uncertain.Contains("NATIVE_CONNECTION_LATE_RELEASE_UNCONFIRMED") && releaseDevice.Releases == 1 &&
            failedRelease.Events.Contains("attempt_1_late_device_release_unconfirmed") &&
            !failedRelease.Events.Contains("attempt_1_late_device_released"));

        var secondLateDevice = new Device(); var secondLate = Success(secondLateDevice); var retryLate = new Harness(Error(), secondLate);
        secondLate.DuringProjection = () => retryLate.InBudget = false;
        check("native retry cannot adopt a second result arriving past the original deadline", await retryLate.RunAsync() is null &&
            retryLate.Begins == 2 && secondLateDevice.Releases == 1 && retryLate.Delays.SequenceEqual([250]));
    }

    private static async Task<Exception?> CaptureAsync(Func<Task<Device?>> action)
    {
        try { await action(); return null; } catch (Exception exception) { return exception; }
    }
    private static Exception ExceptionFor(int code) => new System.Runtime.InteropServices.COMException("Synthetic native failure", code);
    private static Operation Error(int code = NativeConnectionRetry.EnumerationError) =>
        new(Task.FromException<Device?>(ExceptionFor(code)), NativeConnectionOperationState.Error);
    private static Operation Success(Device? value) => new(Task.FromResult(value), NativeConnectionOperationState.Completed);

    private sealed class Device
    {
        internal int Releases;
        internal bool ThrowRelease;
        internal TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Release()
        {
            Releases++; Released.TrySetResult();
            if (ThrowRelease) throw new InvalidOperationException("Synthetic disposal failure");
        }
    }

    private sealed class Operation(Task<Device?> completion, NativeConnectionOperationState state) : INativeConnectionOperation<Device>
    {
        internal int CloseCalls, CancelCalls;
        internal NativeConnectionOperationState CurrentState = state;
        internal bool ThrowState, ThrowClose, ThrowProjection;
        internal Action? DuringProjection, DuringClose, DuringCancel;
        public Task<Device?> Completion
        {
            get
            {
                DuringProjection?.Invoke();
                if (ThrowProjection) throw ExceptionFor(NativeConnectionRetry.EnumerationError);
                return completion;
            }
        }
        public NativeConnectionOperationState State => ThrowState ? throw new InvalidOperationException("Synthetic status failure") : CurrentState;
        public void Cancel() { CancelCalls++; DuringCancel?.Invoke(); }
        public void Close()
        {
            CloseCalls++; DuringClose?.Invoke();
            if (ThrowClose) throw new InvalidOperationException("Synthetic close failure");
        }
    }

    private sealed class Harness(params Operation[] operations)
    {
        internal const string SelectedId = "private-selected-request-id-never-logged";
        internal Operation[] Operations = operations;
        internal int Begins, BoundaryBlockedAt;
        internal bool ExistingAssociation = true, Accepted = true, InBudget = true, ThrowBegin;
        internal Action? DuringDelay;
        internal List<string> Ids { get; } = [];
        internal List<int> Delays { get; } = [];
        internal List<string> Events { get; } = [];
        internal List<string> Uncertain { get; } = [];
        internal Task<Device?> RunAsync(CancellationToken cancellation = default) => NativeConnectionRetry.RunAsync<Device>(SelectedId,
            (id, token) => {
                Begins++; Ids.Add(id);
                if (BoundaryBlockedAt == Begins) throw new NativeConnectionBoundaryException();
                if (ThrowBegin) throw ExceptionFor(NativeConnectionRetry.EnumerationError);
                return Task.FromResult<INativeConnectionOperation<Device>>(Operations[Begins - 1]);
            }, ExistingAssociation, Accepted, () => InBudget, Events.Add, (_, reason) => Uncertain.Add(reason),
            device => device.Release(), cancellation,
            (milliseconds, token) => { Delays.Add(milliseconds); DuringDelay?.Invoke(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; },
            TimeSpan.Zero);
    }
}
