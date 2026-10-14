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

    fun preservationSummary(baselineApKnown: Boolean, comparableSamples: Int, unknownSamples: Int,
                            changedSamples: Int, baselineNetworkLost: Boolean, gapExceeded: Boolean): String = when {
        baselineNetworkLost || changedSamples > 0 -> "change_observed"
        !baselineApKnown || comparableSamples == 0 || unknownSamples > 0 || gapExceeded -> "unknown"
        else -> "same_ap_at_samples_no_loss_event_observed"
    }
}
