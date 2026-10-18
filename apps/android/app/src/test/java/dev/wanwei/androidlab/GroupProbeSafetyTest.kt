package dev.wanwei.androidlab

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class GroupProbeSafetyTest {
    private val own = GroupProbeSafety.Snapshot(true, true, true, true, 2_437, 0, true, true, true)
    private val absent = GroupProbeSafety.Snapshot(false, false, false, null, null, null, false, false, true)

    private fun absence(): GroupProbeSafety.AbsenceProof = GroupProbeSafety.AbsenceProof().also {
        assertTrue(it.observe(1, 100, 110, 111, 111, 100, GroupProbeSafety.Observation.ABSENT))
        assertTrue(it.observe(2, 2_111, 2_120, 2_121, 2_121, 100, GroupProbeSafety.Observation.ABSENT))
        assertTrue(it.confirmed)
    }

    private fun releasable(createAccepted: Boolean? = true, owned: Boolean = true,
                           tainted: Boolean = false, error: Boolean = false, busy: Boolean = false,
                           pending: Boolean = false, removed: Boolean = true,
                           preflight: Boolean = true, evidence: GroupProbeSafety.AbsenceProof = absence()): Boolean =
        GroupProbeSafety.mayReleaseAfterMutation(preflight, createAccepted, owned, tainted, error, busy, pending, removed, evidence)

    @Test fun operatingBandRequestAndActionAcceptanceCannotSubstituteForActualOwnedObservation() {
        assertEquals(GroupProbeSafety.Observation.OWN_EMPTY_24_GHZ, GroupProbeSafety.classify(own))
        assertEquals(GroupProbeSafety.Observation.UNSETTLED, GroupProbeSafety.classify(own.copy(groupFormed = false)))
        assertEquals(GroupProbeSafety.Observation.UNSETTLED, GroupProbeSafety.classify(own.copy(groupFormed = null)))
        assertEquals(GroupProbeSafety.Observation.UNSETTLED, GroupProbeSafety.classify(absent.copy(groupFormed = true)))
        assertEquals(GroupProbeSafety.Observation.UNSETTLED, GroupProbeSafety.classify(absent.copy(groupFormed = null)))
        assertEquals(GroupProbeSafety.Observation.ABSENT, GroupProbeSafety.classify(absent))
        assertFalse(releasable(owned = false))
    }

    @Test fun neighboringGroupsChangedInterfacesClientsAndWrongBandAlwaysTaintOwnership() {
        val foreign = listOf(own.copy(generatedNameMatches = false), own.copy(groupIsOwner = false),
            own.copy(groupIsOwner = null), own.copy(connectionIsOwner = false), own.copy(connectionIsOwner = null),
            own.copy(clientCount = 1), own.copy(clientCount = 2), own.copy(clientCount = null),
            own.copy(usableSignature = false), own.copy(sameSignature = false)) +
            listOf(null, 0, 2_411, 2_485, 5_180, 5_745).map { own.copy(frequencyMhz = it) }
        foreign.forEach { assertEquals("unsafe snapshot $it", GroupProbeSafety.Observation.TAINTED, GroupProbeSafety.classify(it)) }
    }

    @Test fun unknownOriginalApOrObservedApChangePreventsAnyCreation() {
        assertTrue(GroupProbeSafety.mayCreate(true, true, false, false, false, true, false))
        assertFalse(GroupProbeSafety.mayCreate(true, true, false, false, false, false, false))
        assertFalse(GroupProbeSafety.mayCreate(true, true, false, false, false, true, true))
        assertFalse(GroupProbeSafety.mayCreate(false, true, false, false, false, true, false))
        assertFalse(GroupProbeSafety.mayCreate(true, false, false, false, false, true, false))
        assertFalse(GroupProbeSafety.mayCreate(true, true, true, false, false, true, false))
        assertFalse(GroupProbeSafety.mayCreate(true, true, false, true, false, true, false))
        assertFalse(GroupProbeSafety.mayCreate(true, true, false, false, true, true, false))
    }

    @Test fun repeatedBroadcastsAndActionsCannotExtendSetupObservationOrCleanup() {
        val clock = GroupProbeSafety.Windows(500)
        assertEquals(10_500L, clock.setupDeadline)
        assertFalse(clock.expired(10_499))
        assertTrue(clock.beginObservation(10_499))
        assertEquals(20_499L, clock.activeDeadline)
        assertFalse(clock.beginObservation(12_000))
        assertEquals(20_499L, clock.activeDeadline)
        assertTrue(clock.expired(20_499))
        assertTrue(clock.beginCleanup(20_499))
        assertEquals(30_499L, clock.activeDeadline)
        assertFalse(clock.beginCleanup(25_000))
        assertFalse(clock.beginObservation(25_000))
        assertEquals(30_499L, clock.activeDeadline)
        assertFalse(clock.expired(30_498))
        assertTrue(clock.expired(30_499))
    }

    @Test fun exactDeadlineAndClockRollbackCannotStartObservation() {
        assertFalse(GroupProbeSafety.Windows(100).beginObservation(10_100))
        assertFalse(GroupProbeSafety.Windows(100).beginObservation(99))
        val clock = GroupProbeSafety.Windows(100)
        assertTrue(clock.beginCleanup(200))
        assertTrue(clock.expired(199))
        assertFalse(clock.beginCleanup(201))
    }

    @Test fun removalRequiresFreshOwnEmptyGroupAndSettledCreationAtTheCleanupDeadline() {
        assertTrue(GroupProbeSafety.mayRemove(true, true, false, false, GroupProbeSafety.Observation.OWN_EMPTY_24_GHZ, false, false, true))
        assertFalse(GroupProbeSafety.mayRemove(false, true, false, false, GroupProbeSafety.Observation.OWN_EMPTY_24_GHZ, false, false, true))
        assertFalse(GroupProbeSafety.mayRemove(true, false, false, false, GroupProbeSafety.Observation.OWN_EMPTY_24_GHZ, false, false, true))
        assertFalse(GroupProbeSafety.mayRemove(true, true, true, false, GroupProbeSafety.Observation.OWN_EMPTY_24_GHZ, false, false, true))
        assertFalse(GroupProbeSafety.mayRemove(true, true, false, true, GroupProbeSafety.Observation.OWN_EMPTY_24_GHZ, false, false, true))
        assertFalse(GroupProbeSafety.mayRemove(true, true, false, false, GroupProbeSafety.Observation.OWN_EMPTY_24_GHZ, true, false, true))
        assertFalse(GroupProbeSafety.mayRemove(true, true, false, false, GroupProbeSafety.Observation.OWN_EMPTY_24_GHZ, false, true, true))
        assertFalse(GroupProbeSafety.mayRemove(true, true, false, false, GroupProbeSafety.Observation.OWN_EMPTY_24_GHZ, false, false, false))
        listOf(GroupProbeSafety.Observation.ABSENT, GroupProbeSafety.Observation.UNSETTLED, GroupProbeSafety.Observation.TAINTED).forEach {
            assertFalse(GroupProbeSafety.mayRemove(true, true, false, false, it, false, false, true))
        }
    }

    @Test fun lateCreationAckAndFirstCleanupOwnSnapshotOnlyBindSignatureNeverRemoveInTheSameRound() {
        val proof = GroupProbeSafety.OwnershipProof()
        val initial = own.copy(sameSignature = false, signaturePreviouslyObserved = false)
        assertEquals(GroupProbeSafety.Observation.OWN_EMPTY_24_GHZ, GroupProbeSafety.classify(initial))
        proof.observe(10, 100, 110, GroupProbeSafety.classify(initial), false, false)
        assertFalse(proof.removalSupportedBy(10)) // Observed before a late creation acknowledgment.
        proof.observe(11, 200, 210, GroupProbeSafety.classify(initial), true, false)
        assertFalse(proof.removalSupportedBy(11)) // First acknowledged own sample only binds memory.
        assertFalse(GroupProbeSafety.mayRemove(true, true, proof.tainted, false,
            GroupProbeSafety.classify(initial), false, false, proof.removalSupportedBy(11)))
        proof.observe(12, 300, 310, GroupProbeSafety.classify(own), true, true)
        assertTrue(proof.removalSupportedBy(12))
        assertTrue(GroupProbeSafety.mayRemove(true, true, proof.tainted, false,
            GroupProbeSafety.classify(own), false, false, proof.removalSupportedBy(12)))
    }

    @Test fun duplicateOrOverlappingOwnershipQueriesNeverSupplyTheIndependentMatch() {
        val duplicate = GroupProbeSafety.OwnershipProof()
        duplicate.observe(5, 100, 110, GroupProbeSafety.Observation.OWN_EMPTY_24_GHZ, true, false)
        duplicate.observe(5, 200, 210, GroupProbeSafety.Observation.OWN_EMPTY_24_GHZ, true, true)
        assertFalse(duplicate.removalSupportedBy(5))
        val overlapping = GroupProbeSafety.OwnershipProof()
        overlapping.observe(5, 100, 110, GroupProbeSafety.Observation.OWN_EMPTY_24_GHZ, true, false)
        overlapping.observe(6, 109, 120, GroupProbeSafety.Observation.OWN_EMPTY_24_GHZ, true, true)
        assertFalse(overlapping.removalSupportedBy(6))
        assertTrue(overlapping.tainted)
    }

    @Test fun unknownNativePairBeforeOwnGroupPermanentlyBlocksRemovalAndLaterAbsenceUnlock() {
        val uncertain = listOf(absent.copy(groupFormed = null), absent.copy(groupFormed = true),
            absent.copy(connectionIsOwner = true), absent.copy(connectionIsOwner = null), own.copy(groupFormed = false))
        uncertain.forEach { snapshot ->
            val observation = GroupProbeSafety.classify(snapshot)
            assertEquals(GroupProbeSafety.Observation.UNSETTLED, observation)
            val proof = GroupProbeSafety.OwnershipProof()
            proof.observe(1, 100, 110, observation, true, false)
            proof.observe(2, 200, 210, GroupProbeSafety.Observation.OWN_EMPTY_24_GHZ, true, false)
            proof.observe(3, 300, 310, GroupProbeSafety.Observation.OWN_EMPTY_24_GHZ, true, true)
            assertTrue(proof.tainted)
            assertFalse(proof.removalSupportedBy(3))
            assertFalse(releasable(tainted = proof.tainted))
        }
    }

    @Test fun removalAckThenUnknownThenFreshAbsenceCannotTurnCleanupIntoSuccess() {
        val proof = GroupProbeSafety.OwnershipProof()
        proof.observe(1, 100, 110, GroupProbeSafety.Observation.OWN_EMPTY_24_GHZ, true, false)
        proof.observe(2, 200, 210, GroupProbeSafety.Observation.OWN_EMPTY_24_GHZ, true, true)
        assertTrue(proof.removalSupportedBy(2))
        // Even with a removal acknowledgment, an intervening contradictory/unknown native pair is sticky.
        proof.observe(3, 300, 310, GroupProbeSafety.Observation.UNSETTLED, true, false)
        proof.observe(4, 400, 410, GroupProbeSafety.Observation.ABSENT, true, false)
        proof.observe(5, 2_500, 2_510, GroupProbeSafety.Observation.ABSENT, true, false)
        assertTrue(proof.tainted)
        assertFalse(releasable(tainted = proof.tainted, removed = true, evidence = absence()))
    }

    @Test fun contradictoryPreflightInfoDoesNotAuthorizeCreatingAnyGroup() {
        assertTrue(GroupProbeSafety.preflightAbsent(false, false, false))
        assertFalse(GroupProbeSafety.preflightAbsent(false, false, true))
        assertFalse(GroupProbeSafety.preflightAbsent(false, false, null))
        assertFalse(GroupProbeSafety.preflightAbsent(false, null, false))
        assertFalse(GroupProbeSafety.preflightAbsent(false, true, true))
        assertFalse(GroupProbeSafety.preflightAbsent(true, false, false))
    }

    @Test fun knownInitialBaselineCannotAuthorizeCreationAfterTheNewApSampleBecomesUnknownDifferentOrStale() {
        assertTrue(GroupProbeSafety.originalAssociationReady(true, true, true, true, true, false))
        assertFalse(GroupProbeSafety.originalAssociationReady(true, false, true, true, true, false))
        assertFalse(GroupProbeSafety.originalAssociationReady(true, true, false, true, true, false))
        assertFalse(GroupProbeSafety.originalAssociationReady(true, true, true, false, true, false))
        assertFalse(GroupProbeSafety.originalAssociationReady(true, true, true, true, false, false))
        assertFalse(GroupProbeSafety.originalAssociationReady(true, true, true, true, true, true))
        assertFalse(GroupProbeSafety.originalAssociationReady(false, true, true, true, true, false))
    }

    @Test fun ephemeralNetworkNameFitsSsidBytesAndRequiredDirectPrefixWithoutTestingTheRandomGenerator() {
        val name = GroupProbeSafety.temporaryNetworkName("A1", "aB09aB09aB09")
        assertTrue(Regex("DIRECT-[A-Za-z0-9]{2}-Wanwei-[A-Za-z0-9]{12}").matches(name))
        assertEquals(29, name.toByteArray(Charsets.UTF_8).size)
        assertTrue(name.toByteArray(Charsets.UTF_8).size <= 32)
        listOf("", "A", "ABC", "中1", "A-").forEach {
            assertTrue(runCatching { GroupProbeSafety.temporaryNetworkName(it, "aB09aB09aB09") }.isFailure)
        }
        listOf("", "aB09aB09aB0", "aB09aB09aB09aB09", "中B09aB09aB09").forEach {
            assertTrue(runCatching { GroupProbeSafety.temporaryNetworkName("A1", it) }.isFailure)
        }
    }

    @Test fun freshEmptyReadsCannotEraseAcceptedNeverObservedOrBusyUncertainty() {
        assertTrue(releasable())
        assertFalse(releasable(createAccepted = null))
        assertFalse(releasable(owned = false))
        assertFalse(releasable(removed = false))
        assertFalse(releasable(pending = true))
        assertFalse(releasable(busy = true))
        assertFalse(releasable(tainted = true))
        assertFalse(releasable(error = true))
        assertFalse(releasable(preflight = false))
        assertFalse(releasable(evidence = GroupProbeSafety.AbsenceProof()))
        // Only a definitive non-BUSY rejection can finish without ever claiming group ownership/removal.
        assertTrue(releasable(createAccepted = false, owned = false, removed = false))
        assertFalse(releasable(createAccepted = false, owned = false, removed = false, busy = true))
    }

    @Test fun lateDuplicateAndPartialQueriesCannotCreateAnAbsenceWindow() {
        val proof = GroupProbeSafety.AbsenceProof()
        assertFalse(proof.observe(1, 99, 110, 110, 110, 100, GroupProbeSafety.Observation.ABSENT))
        assertTrue(proof.observe(2, 100, 110, 111, 111, 100, GroupProbeSafety.Observation.ABSENT))
        assertFalse(proof.observe(2, 2_111, 2_120, 2_121, 2_121, 100, GroupProbeSafety.Observation.ABSENT))
        assertEquals(1, proof.samples)
        assertFalse(proof.observe(3, 2_111, null, 2_121, 2_121, 100, GroupProbeSafety.Observation.ABSENT))
        assertEquals(0, proof.samples)
        assertFalse(proof.confirmed)
        assertFalse(proof.observe(4, 3_000, 4_501, 4_501, 4_501, 100, GroupProbeSafety.Observation.ABSENT))
        assertFalse(proof.observe(5, 5_000, 4_999, 5_001, 5_001, 100, GroupProbeSafety.Observation.ABSENT))
        assertFalse(proof.confirmed)
    }

    @Test fun absenceRequiresTwoSecondsOfDistinctNonoverlappingCallbacksWithoutUnknownGaps() {
        val proof = GroupProbeSafety.AbsenceProof()
        assertTrue(proof.observe(1, 100, 110, 111, 111, 100, GroupProbeSafety.Observation.ABSENT))
        assertTrue(proof.observe(2, 2_000, 2_010, 2_011, 2_011, 100, GroupProbeSafety.Observation.ABSENT))
        assertFalse(proof.confirmed)
        assertTrue(proof.observe(3, 2_111, 2_120, 2_121, 2_121, 100, GroupProbeSafety.Observation.ABSENT))
        assertTrue(proof.confirmed)
        assertFalse(proof.observe(4, 2_120, 2_130, 2_131, 2_131, 100, GroupProbeSafety.Observation.ABSENT))
        assertFalse(proof.confirmed)
        assertTrue(proof.observe(5, 3_000, 3_010, 3_011, 3_011, 100, GroupProbeSafety.Observation.ABSENT))
        assertTrue(proof.observe(6, 6_100, 6_110, 6_111, 6_111, 100, GroupProbeSafety.Observation.ABSENT))
        assertEquals(1, proof.samples)
        assertFalse(proof.confirmed)
        assertFalse(proof.observe(7, 7_000, 7_010, 7_011, 7_011, 100, GroupProbeSafety.Observation.UNSETTLED))
        assertEquals(0, proof.samples)
    }

    @Test fun cleanupUnknownCannotBeBypassedByActivityRecreationOrAnotherRadioMode() {
        val gate = RadioSessionGate()
        assertTrue(gate.acquire("probe"))
        assertFalse(gate.acquire("pairing"))
        assertFalse(releasable(createAccepted = true, owned = false, removed = false))
        assertTrue(gate.release("probe", true))
        assertTrue(gate.locked())
        assertFalse(gate.acquire("replacement-activity"))
    }
}
