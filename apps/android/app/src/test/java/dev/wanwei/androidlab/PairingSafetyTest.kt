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

    private fun absent(evidence: PairingSafety.EstablishedAbsenceEvidence, query: Int, requested: Long,
                       completed: Long = requested + 10): Boolean = evidence.observe(query, requested,
        completed, completed, completed, 0, true, false)

    private fun provenAbsence(): PairingSafety.EstablishedAbsenceEvidence =
        PairingSafety.EstablishedAbsenceEvidence().also {
            absent(it, 1, 0)
            absent(it, 2, 2_000)
            assertTrue(it.confirmed)
        }

    @Test fun establishedLossCanReleaseItsGateWithoutInventingCancellationSuccess() {
        val gate = RadioSessionGate()
        val evidence = PairingSafety.EstablishedAbsenceEvidence()
        assertTrue(gate.acquire("completed-connection"))
        assertTrue(absent(evidence, 1, 0))
        assertFalse(evidence.confirmed)
        assertFalse(gate.acquire("next-attempt"))
        absent(evidence, 2, 1_999)
        assertFalse(evidence.confirmed)
        absent(evidence, 3, 2_010)
        assertTrue(evidence.confirmed)
        assertTrue(PairingSafety.mayConfirmEstablishedAbsence(true, true, false, false, true, true, false, evidence))
        assertTrue(gate.release("completed-connection", false))
        assertTrue(gate.acquire("next-attempt"))
        // Releasing our process gate never bypasses the next attempt's native preflight.
        assertFalse(PairingSafety.mayStart(false, true, true, true, false))
        assertFalse(PairingSafety.mayStart(true, false, true, true, false))
    }

    @Test fun neverEstablishedForeignSharedOrFaultedSessionsCannotUseAbsenceShortcut() {
        val evidence = provenAbsence()
        assertFalse(PairingSafety.mayConfirmEstablishedAbsence(true, false, false, false, true, true, false, evidence))
        assertFalse(PairingSafety.mayConfirmEstablishedAbsence(false, true, false, false, true, true, false, evidence))
        assertFalse(PairingSafety.mayConfirmEstablishedAbsence(true, true, true, false, true, true, false, evidence))
        assertFalse(PairingSafety.mayConfirmEstablishedAbsence(true, true, false, true, true, true, false, evidence))
        assertFalse(PairingSafety.mayConfirmEstablishedAbsence(true, true, false, false, false, true, false, evidence))
        assertFalse(PairingSafety.mayConfirmEstablishedAbsence(true, true, false, false, true, false, false, evidence))
        assertFalse(PairingSafety.mayConfirmEstablishedAbsence(true, true, false, false, true, true, true, evidence))
        assertFalse(PairingSafety.mayConfirmEstablishedAbsence(true, true, false, false, true, true, false, PairingSafety.EstablishedAbsenceEvidence()))
    }

    @Test fun duplicateOutOfOrderOrPreCleanupQueriesCannotAdvanceProof() {
        val evidence = PairingSafety.EstablishedAbsenceEvidence()
        absent(evidence, 10, 0)
        assertFalse(absent(evidence, 10, 2_000))
        assertFalse(absent(evidence, 9, 2_001))
        assertEquals(1, evidence.consecutiveSamples)
        assertFalse(evidence.confirmed)
        assertFalse(evidence.observe(11, 2_000, 2_010, 2_010, 2_010, 2_001, true, false))
        assertEquals(0, evidence.consecutiveSamples)
    }

    @Test fun unknownPartialTimeoutAndReappearingGroupsResetTheAbsenceWindow() {
        val evidence = PairingSafety.EstablishedAbsenceEvidence()
        absent(evidence, 1, 0)
        assertFalse(evidence.observe(2, 1_000, 1_010, null, 1_010, 0, true, false))
        assertEquals(0, evidence.consecutiveSamples)
        absent(evidence, 3, 2_000)
        assertFalse(evidence.observe(4, 3_000, 3_010, 3_010, 3_010, 0, true, null))
        absent(evidence, 5, 4_000)
        assertFalse(evidence.observe(6, 5_000, 6_501, 6_501, 6_501, 0, true, false))
        absent(evidence, 7, 7_000)
        assertFalse(evidence.observe(8, 8_000, 8_010, 8_010, 8_010, 0, false, true))
        assertFalse(evidence.confirmed)
        absent(evidence, 9, 9_000)
        absent(evidence, 10, 11_000)
        assertTrue(evidence.confirmed)
    }

    @Test fun longUnobservedGapsOverlappingRoundsAndClockRollbackCannotBridgeAbsence() {
        val evidence = PairingSafety.EstablishedAbsenceEvidence()
        absent(evidence, 1, 0)
        absent(evidence, 2, 3_001)
        assertEquals(1, evidence.consecutiveSamples)
        assertFalse(evidence.confirmed)
        absent(evidence, 3, 3_005, 3_100) // Starts before the previous round completed: new window.
        assertEquals(1, evidence.consecutiveSamples)
        absent(evidence, 4, 3_000, 3_010)
        assertFalse(evidence.confirmed)
        assertEquals(1, evidence.consecutiveSamples)
    }

    @Test fun permissionOrChannelUncertaintyKeepsProcessLockedEvenWithTwoAbsentReads() {
        val gate = RadioSessionGate()
        assertTrue(gate.acquire("faulted-session"))
        assertFalse(PairingSafety.mayConfirmEstablishedAbsence(true, true, false, true, true, true, false, provenAbsence()))
        assertTrue(gate.release("faulted-session", true))
        assertTrue(gate.locked())
        assertFalse(gate.acquire("next-session"))
    }
}
