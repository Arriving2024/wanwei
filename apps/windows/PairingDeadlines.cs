namespace Wanwei;

/// <summary>Monotonic, one-way phase deadlines. The caller serializes changes with its radio gate.</summary>
internal sealed class PairingDeadlines
{
    internal const int NegotiationLimitMs = 45_000;
    internal const int ObservationLimitMs = 20_000;
    private readonly List<PairingPhaseEvidence> phases = [];
    internal int WaitLimitMs { get; }
    internal int MaximumRunMs => WaitLimitMs + NegotiationLimitMs + ObservationLimitMs;
    internal string Phase { get; private set; } = "not_started";
    internal string? EndReason { get; private set; }
    internal long? PublisherStartedElapsedMs { get; private set; }
    internal long? OverallDeadlineElapsedMs { get; private set; }
    internal long? EndedElapsedMs { get; private set; }
    internal bool Running => Phase is "waiting_for_request" or "negotiating" or "observing";
    internal long? DeadlineElapsedMs => phases.Count == 0 ? null : phases[^1].DeadlineElapsedMs;

    internal PairingDeadlines(int waitSeconds)
    {
        if (waitSeconds is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(waitSeconds));
        WaitLimitMs = waitSeconds * 1_000;
    }

    internal bool Start(long now)
    {
        if (Phase != "not_started") return false;
        PublisherStartedElapsedMs = now;
        OverallDeadlineElapsedMs = now + MaximumRunMs;
        Enter("waiting_for_request", now, WaitLimitMs);
        return true;
    }

    internal bool TrySelectRequest(long now)
    {
        if (Phase != "waiting_for_request" || !BeforeDeadline(now)) return false;
        Enter("negotiating", now, NegotiationLimitMs);
        return true;
    }

    internal bool TryObserveConnection(long now)
    {
        if (Phase != "negotiating" || !BeforeDeadline(now)) return false;
        Enter("observing", now, ObservationLimitMs);
        return true;
    }

    internal bool Expire(long now)
    {
        if (!Running || now < DeadlineElapsedMs) return false;
        return Stop(now, Phase switch {
            "waiting_for_request" => "wait_for_request_timeout",
            "negotiating" => "negotiation_timeout",
            _ => "observation_window_completed"
        });
    }

    internal bool Stop(long now, string reason)
    {
        if (Phase == "finished") return false;
        ClosePhase(now);
        EndedElapsedMs = now;
        EndReason = reason;
        Phase = "finished";
        return true;
    }

    internal PairingTimingEvidence Snapshot() => new(WaitLimitMs, NegotiationLimitMs,
        ObservationLimitMs, MaximumRunMs, PublisherStartedElapsedMs, OverallDeadlineElapsedMs,
        EndedElapsedMs, EndReason, phases.ToArray());

    private bool BeforeDeadline(long now) => Running && now >= phases[^1].StartedElapsedMs &&
        now < DeadlineElapsedMs && now < OverallDeadlineElapsedMs;

    private void Enter(string phase, long now, int durationMs)
    {
        ClosePhase(now);
        Phase = phase;
        phases.Add(new(phase, now, Math.Min(now + durationMs, OverallDeadlineElapsedMs!.Value), null));
    }

    private void ClosePhase(long now)
    {
        if (phases.Count > 0 && phases[^1].EndedElapsedMs is null)
            phases[^1] = phases[^1] with { EndedElapsedMs = now };
    }
}
