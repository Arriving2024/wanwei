package dev.wanwei.androidlab

/** Pure policy used by the radio coordinator and host tests. Unknown never authorizes removal. */
internal object PairingSafety {
    fun isUsableBssid(value: String?): Boolean = value != null &&
        Regex("(?i)^[0-9a-f]{2}(:[0-9a-f]{2}){5}$").matches(value) &&
        value != "02:00:00:00:00:00" && value != "00:00:00:00:00:00" &&
        !value.equals("ff:ff:ff:ff:ff:ff", true)

    fun mayStart(preflightGroupAbsent: Boolean, preflightInfoAbsent: Boolean,
                 candidateFresh: Boolean, candidateAvailable: Boolean, locked: Boolean): Boolean =
        preflightGroupAbsent && preflightInfoAbsent && candidateFresh && candidateAvailable && !locked

    fun mayRemove(preflightClear: Boolean, connectIssued: Boolean, targetMatches: Boolean,
                  extraParticipants: Boolean, sameObservedGroup: Boolean): Boolean =
        preflightClear && connectIssued && targetMatches && !extraParticipants && sameObservedGroup

    /** Native group disappearance is distinct from acknowledging cancelConnect. */
    fun mayConfirmEstablishedAbsence(preflightClear: Boolean, ownedGroupWasEstablished: Boolean,
                                    ownershipTainted: Boolean, callbackError: Boolean,
                                    discoveryStopConfirmed: Boolean, connectRequestAcknowledged: Boolean,
                                    operationPending: Boolean, absence: EstablishedAbsenceEvidence): Boolean =
        preflightClear && ownedGroupWasEstablished && !ownershipTainted && !callbackError &&
            discoveryStopConfirmed && connectRequestAcknowledged && !operationPending && absence.confirmed

    /** Only completed, non-overlapping query pairs from the cleanup window can advance proof. */
    class EstablishedAbsenceEvidence {
        var consecutiveSamples = 0
            private set
        var firstObservedAtMs: Long? = null
            private set
        var lastObservedAtMs: Long? = null
            private set
        var lastQueryId = -1
            private set
        val observedSpanMs: Long get() = if (firstObservedAtMs != null && lastObservedAtMs != null)
            lastObservedAtMs!! - firstObservedAtMs!! else 0L
        val confirmed: Boolean get() = consecutiveSamples >= 2 && observedSpanMs >= 2_000

        fun reset() { consecutiveSamples = 0; firstObservedAtMs = null; lastObservedAtMs = null }

        fun observe(queryId: Int, requestedAtMs: Long, groupCallbackAtMs: Long?, infoCallbackAtMs: Long?,
                    completedAtMs: Long, cleanupStartedAtMs: Long, groupAbsent: Boolean,
                    groupFormed: Boolean?): Boolean {
            if (queryId <= lastQueryId) return false // A late/duplicate query never extends the span.
            lastQueryId = queryId
            val fresh = requestedAtMs >= cleanupStartedAtMs && groupCallbackAtMs != null && infoCallbackAtMs != null &&
                groupCallbackAtMs in requestedAtMs..completedAtMs && infoCallbackAtMs in requestedAtMs..completedAtMs &&
                completedAtMs >= requestedAtMs && completedAtMs - requestedAtMs <= 1_500
            if (!fresh || !groupAbsent || groupFormed != false) { reset(); return false }
            val previous = lastObservedAtMs
            if (previous != null && (requestedAtMs < previous || completedAtMs < previous || completedAtMs - previous > 3_000)) reset()
            if (firstObservedAtMs == null) firstObservedAtMs = completedAtMs
            lastObservedAtMs = completedAtMs
            consecutiveSamples++
            return true
        }
    }

    fun preservationSummary(baselineApKnown: Boolean, comparableSamples: Int, unknownSamples: Int,
                            changedSamples: Int, baselineNetworkLost: Boolean, gapExceeded: Boolean): String = when {
        baselineNetworkLost || changedSamples > 0 -> "change_observed"
        !baselineApKnown || comparableSamples == 0 || unknownSamples > 0 || gapExceeded -> "unknown"
        else -> "same_ap_at_samples_no_loss_event_observed"
    }
}
