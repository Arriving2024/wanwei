package dev.wanwei.androidlab

/** Public-API LOHS policy. Releasing our request is never proof that the global AP disappeared. */
internal object LocalHotspotSafety {
    const val SETUP_MS = 15_000L
    const val ACTIVE_MS = 120_000L
    const val GLOBAL_AP_ABSENCE = "unknown_not_observable_via_public_api"

    // Public SoftApConfiguration security values; kept Android-free for the host policy tests.
    private val supportedSecurity = setOf(1, 2, 3) // WPA2-PSK, WPA3-SAE transition, WPA3-SAE.

    fun usableCredentials(ssid: String?, passphrase: String?, securityType: Int): Boolean {
        if (securityType !in supportedSecurity || ssid.isNullOrBlank() || passphrase == null) return false
        val bytes = ssid.toByteArray(Charsets.UTF_8)
        if (bytes.size !in 1..32 || String(bytes, Charsets.UTF_8) != ssid ||
            ssid.any { it.code < 0x20 || it.code == 0x7f } || ssid == "<unknown ssid>") return false
        // A generated LOHS password is ASCII. Reject surprising OEM data rather than expose an open/invalid AP.
        return passphrase.length in 8..63 && passphrase.all { it.code in 0x20..0x7e }
    }

    enum class Phase { PENDING, ACTIVE, RELEASING, FINISHED }
    enum class StartedAction { ACTIVATE, CLOSE_OWN_RESERVATION, CLOSE_STALE_RESERVATION }

    class Lifecycle(val generation: Long, val startedAt: Long) {
        val setupDeadline = startedAt + SETUP_MS
        var activeDeadline: Long? = null
            private set
        var phase = Phase.PENDING
            private set
        var uncertain = false
            private set
        var ownRequestReleased = false
            private set
        var failureObserved = false
            private set

        /** The first accepted callback fixes the deadline; duplicates and later events cannot extend it. */
        fun started(callbackGeneration: Long, now: Long, credentialsValid: Boolean): StartedAction {
            if (callbackGeneration != generation) return StartedAction.CLOSE_STALE_RESERVATION
            if (phase != Phase.PENDING || uncertain) return StartedAction.CLOSE_OWN_RESERVATION
            if (now < startedAt || now >= setupDeadline) {
                uncertain = true
                phase = Phase.RELEASING
                return StartedAction.CLOSE_OWN_RESERVATION
            }
            if (!credentialsValid) {
                phase = Phase.RELEASING
                return StartedAction.CLOSE_OWN_RESERVATION
            }
            activeDeadline = now + ACTIVE_MS
            phase = Phase.ACTIVE
            return StartedAction.ACTIVATE
        }

        /** There is no public pending-request cancellation; late failure/release must not erase this uncertainty. */
        fun stopPending(): Boolean {
            if (phase != Phase.PENDING) return false
            uncertain = true
            phase = Phase.FINISHED
            return true
        }

        fun failed(callbackGeneration: Long): Boolean {
            if (callbackGeneration != generation) return false
            if (phase == Phase.ACTIVE || phase == Phase.RELEASING) uncertain = true
            failureObserved = true
            phase = Phase.FINISHED
            return true
        }

        fun released(callbackGeneration: Long, closeReturned: Boolean): Boolean {
            if (callbackGeneration != generation) return false
            ownRequestReleased = ownRequestReleased || closeReturned
            if (!closeReturned) uncertain = true
            phase = Phase.FINISHED
            return true
        }

        fun markUncertain() { uncertain = true }
        fun activeExpired(now: Long): Boolean = activeDeadline?.let { now < startedAt || now >= it } == true
    }
}
