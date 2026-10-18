package dev.wanwei.androidlab

/** Pure rules for the isolated, local-GO probe. Missing evidence never grants ownership. */
internal object GroupProbeSafety {
    const val SETUP_MS = 10_000L
    const val OBSERVATION_MS = 10_000L
    const val CLEANUP_MS = 10_000L
    const val QUERY_FRESH_MS = 1_500L
    const val RANDOM_PREFIX_LENGTH = 2
    const val RANDOM_SUFFIX_LENGTH = 12
    const val PASSPHRASE_LENGTH = 32

    fun temporaryNetworkName(prefix: String, suffix: String): String {
        require(Regex("[A-Za-z0-9]{$RANDOM_PREFIX_LENGTH}").matches(prefix))
        require(Regex("[A-Za-z0-9]{$RANDOM_SUFFIX_LENGTH}").matches(suffix))
        return "DIRECT-$prefix-Wanwei-$suffix" // 29 ASCII bytes; IEEE 802.11 SSID maximum is 32.
    }

    enum class Observation { ABSENT, OWN_EMPTY_24_GHZ, UNSETTLED, TAINTED }

    data class Snapshot(
        val groupPresent: Boolean,
        val groupFormed: Boolean?,
        val connectionIsOwner: Boolean?,
        val groupIsOwner: Boolean?,
        val frequencyMhz: Int?,
        val clientCount: Int?,
        val generatedNameMatches: Boolean,
        val usableSignature: Boolean,
        val sameSignature: Boolean,
        val signaturePreviouslyObserved: Boolean = true
    )

    fun classify(value: Snapshot): Observation {
        if (!value.groupPresent) return if (preflightAbsent(value.groupPresent, value.groupFormed, value.connectionIsOwner))
            Observation.ABSENT else Observation.UNSETTLED
        if (value.groupIsOwner != true || value.frequencyMhz == null || value.frequencyMhz !in 2_412..2_484 || value.clientCount != 0 ||
            !value.generatedNameMatches || !value.usableSignature ||
            (value.signaturePreviouslyObserved && !value.sameSignature)) return Observation.TAINTED
        if (value.groupFormed != true) return Observation.UNSETTLED
        return if (value.connectionIsOwner == true) Observation.OWN_EMPTY_24_GHZ else Observation.TAINTED
    }

    fun preflightAbsent(groupPresent: Boolean, formed: Boolean?, isOwner: Boolean?): Boolean =
        !groupPresent && formed == false && isOwner == false

    fun freshPair(requestedAt: Long, groupAt: Long?, infoAt: Long?, completedAt: Long,
                  previousCompletedAt: Long?): Boolean = requestedAt >= 0 && groupAt != null && infoAt != null &&
        completedAt >= requestedAt && completedAt - requestedAt <= QUERY_FRESH_MS &&
        groupAt in requestedAt..completedAt && infoAt in requestedAt..completedAt &&
        (previousCompletedAt == null || requestedAt >= previousCompletedAt)

    fun mayCreate(preflightAbsent: Boolean, preflightFresh: Boolean, gateLocked: Boolean,
                  queryPending: Boolean, setupExpired: Boolean, originalWifiAssociationReady: Boolean,
                  originalWifiChangeObserved: Boolean): Boolean =
        preflightAbsent && preflightFresh && !gateLocked && !queryPending && !setupExpired &&
            originalWifiAssociationReady && !originalWifiChangeObserved

    fun originalAssociationReady(baselineKnown: Boolean, currentApObserved: Boolean,
                                 currentApMatchesBaseline: Boolean, originalNetworkStillObserved: Boolean,
                                 sampleFresh: Boolean, lossOrChangeObserved: Boolean): Boolean =
        baselineKnown && currentApObserved && currentApMatchesBaseline && originalNetworkStillObserved &&
            sampleFresh && !lossOrChangeObserved

    fun mayRemove(preflightClear: Boolean, createAcknowledged: Boolean, ownershipTainted: Boolean,
                  callbackError: Boolean, observation: Observation, operationPending: Boolean,
                  cleanupExpired: Boolean, independentMatchingObservation: Boolean): Boolean =
        preflightClear && createAcknowledged && !ownershipTainted && !callbackError &&
            observation == Observation.OWN_EMPTY_24_GHZ && !operationPending && !cleanupExpired && independentMatchingObservation

    /** Binding a signature and granting removal are separate query rounds. Unknown is sticky. */
    class OwnershipProof {
        var tainted = false
            private set
        private var boundQueryId: Int? = null
        private var boundCompletedAt: Long? = null
        private var approvedQueryId: Int? = null
        private var lastQueryId = -1
        private var lastCompletedAt: Long? = null
        fun taint() { tainted = true; approvedQueryId = null }
        fun observe(queryId: Int, requestedAt: Long, completedAt: Long, observation: Observation,
                    createAcknowledged: Boolean, matchesPreviouslyBoundSignature: Boolean) {
            approvedQueryId = null
            if (queryId <= lastQueryId) return
            lastQueryId = queryId
            if (!freshPair(requestedAt, completedAt, completedAt, completedAt, lastCompletedAt)) { taint(); return }
            lastCompletedAt = completedAt
            if (observation == Observation.UNSETTLED || observation == Observation.TAINTED) taint()
            if (tainted || observation != Observation.OWN_EMPTY_24_GHZ || !createAcknowledged) return
            if (boundQueryId == null) {
                boundQueryId = queryId; boundCompletedAt = completedAt
                return // First ownership candidate can bind memory, never remove in this round.
            }
            if (!matchesPreviouslyBoundSignature) { taint(); return }
            if (queryId <= boundQueryId!! || requestedAt < boundCompletedAt!! || completedAt < requestedAt) return
            approvedQueryId = queryId
        }
        fun removalSupportedBy(queryId: Int): Boolean = !tainted && approvedQueryId == queryId
    }

    /** Acceptance without an observed own group remains unsettled even when later queries say absent. */
    fun mayReleaseAfterMutation(preflightClear: Boolean, createAccepted: Boolean?, ownedGroupObserved: Boolean,
                                ownershipTainted: Boolean, callbackError: Boolean, createRejectedBusy: Boolean,
                                operationPending: Boolean,
                                removalAccepted: Boolean, absence: AbsenceProof): Boolean {
        if (!preflightClear || ownershipTainted || callbackError || createRejectedBusy || operationPending ||
            !absence.confirmed) return false
        return when (createAccepted) {
            false -> true // A definitive non-BUSY rejection never established our group.
            true -> ownedGroupObserved && removalAccepted
            null -> false
        }
    }

    /** Absolute phase deadlines are written once. Events cannot refresh the current window. */
    class Windows(val startedAt: Long) {
        val setupDeadline = startedAt + SETUP_MS
        var observationStartedAt: Long? = null
            private set
        var cleanupStartedAt: Long? = null
            private set
        val observationDeadline: Long? get() = observationStartedAt?.plus(OBSERVATION_MS)
        val cleanupDeadline: Long? get() = cleanupStartedAt?.plus(CLEANUP_MS)
        val activeDeadline: Long get() = cleanupDeadline ?: observationDeadline ?: setupDeadline
        fun expired(now: Long): Boolean = now < (cleanupStartedAt ?: observationStartedAt ?: startedAt) || now >= activeDeadline
        fun beginObservation(now: Long): Boolean {
            if (cleanupStartedAt != null || observationStartedAt != null || expired(now)) return false
            observationStartedAt = now
            return true
        }
        fun beginCleanup(now: Long): Boolean {
            if (cleanupStartedAt != null || now < startedAt) return false
            cleanupStartedAt = now
            return true
        }
    }

    /** Consecutive, non-overlapping native query pairs collected after cleanup started. */
    class AbsenceProof {
        var samples = 0
            private set
        private var lastId = -1
        private var firstAt: Long? = null
        private var lastAt: Long? = null
        val observedSpanMs: Long get() = if (firstAt != null && lastAt != null) lastAt!! - firstAt!! else 0
        val confirmed: Boolean get() = samples >= 2 && observedSpanMs >= 2_000
        fun reset() { samples = 0; firstAt = null; lastAt = null }
        fun observe(queryId: Int, requestedAt: Long, groupAt: Long?, infoAt: Long?, completedAt: Long,
                    cleanupAt: Long, observation: Observation): Boolean {
            if (queryId <= lastId) return false
            lastId = queryId
            if (requestedAt < cleanupAt || !freshPair(requestedAt, groupAt, infoAt, completedAt, lastAt) ||
                observation != Observation.ABSENT) { reset(); return false }
            if (lastAt != null && completedAt - lastAt!! > 3_000) reset()
            if (firstAt == null) firstAt = completedAt
            lastAt = completedAt
            samples++
            return true
        }
    }
}
