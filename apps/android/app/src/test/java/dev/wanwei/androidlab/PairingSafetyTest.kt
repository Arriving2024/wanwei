package dev.wanwei.androidlab

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class PairingSafetyTest {
    @Test fun recreationCannotOverlapOldCleanupOrClearUncertainState() {
        val gate = RadioSessionGate()
        assertTrue(gate.acquire("old-activity"))
        assertFalse(gate.acquire("new-activity"))
        assertFalse(gate.release("new-activity", false))
        assertTrue(gate.occupied())
        assertTrue(gate.release("old-activity", false))
        assertTrue(gate.acquire("new-activity"))
        assertTrue(gate.release("new-activity", true))
        assertTrue(gate.locked())
        assertFalse(gate.acquire("third-activity"))
    }
    @Test fun existingUnknownStaleBusyOrUnavailableNeverStart() {
        for (mask in 0..31) {
            val groupClear = mask and 1 != 0
            val infoClear = mask and 2 != 0
            val fresh = mask and 4 != 0
            val available = mask and 8 != 0
            val locked = mask and 16 != 0
            assertEquals("start mask=$mask", mask == 15,
                PairingSafety.mayStart(groupClear, infoClear, fresh, available, locked))
        }
    }
    @Test fun unknownOwnershipOrOtherParticipantsNeverAuthorizeRemoval() {
        for (mask in 0..31) {
            val preflight = mask and 1 != 0
            val issued = mask and 2 != 0
            val target = mask and 4 != 0
            val extra = mask and 8 != 0
            val same = mask and 16 != 0
            assertEquals("remove mask=$mask", mask == 23,
                PairingSafety.mayRemove(preflight, issued, target, extra, same))
        }
    }
    @Test fun preservationNeverConflatesUnknownWithContinuousInternet() {
        assertEquals("unknown", PairingSafety.preservationSummary(false, 30, 0, 0, false, false))
        assertEquals("unknown", PairingSafety.preservationSummary(true, 0, 0, 0, false, false))
        assertEquals("unknown", PairingSafety.preservationSummary(true, 30, 1, 0, false, false))
        assertEquals("unknown", PairingSafety.preservationSummary(true, 30, 0, 0, false, true))
        assertEquals("change_observed", PairingSafety.preservationSummary(true, 30, 0, 1, false, false))
        assertEquals("change_observed", PairingSafety.preservationSummary(false, 0, 30, 0, true, false))
        assertEquals("same_ap_at_samples_no_loss_event_observed", PairingSafety.preservationSummary(true, 30, 0, 0, false, false))
    }
    @Test fun redactedAndMalformedAddressesCannotBecomeApIdentity() {
        listOf(null, "", "unknown", "<unknown ssid>", "02:00:00:00:00:00", "00:00:00:00:00:00", "FF:FF:FF:FF:FF:FF", "ab:cd:ef", "gh:cd:ef:12:34:56").forEach {
            assertFalse("must remain unknown: $it", PairingSafety.isUsableBssid(it))
        }
        assertTrue(PairingSafety.isUsableBssid("ab:cd:ef:12:34:56"))
        assertTrue(PairingSafety.isUsableBssid("12:AB:CD:EF:34:56"))
    }
}
