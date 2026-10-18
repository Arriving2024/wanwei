package dev.wanwei.androidlab

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class LocalHotspotSafetyTest {
    @Test fun onlyPersonalPasswordSecurityTypesWithUsableCredentialsAreAccepted() {
        listOf(1, 2, 3).forEach { assertTrue(LocalHotspotSafety.usableCredentials("Wanwei_5G", "12345678", it)) }
        listOf(-1, 0, 4, 5, 99).forEach { assertFalse(LocalHotspotSafety.usableCredentials("Wanwei_5G", "12345678", it)) }
        assertFalse(LocalHotspotSafety.usableCredentials("Wanwei_5G", null, 1))
        assertFalse(LocalHotspotSafety.usableCredentials("Wanwei_5G", "", 1))
    }

    @Test fun ssidUsesActualUtf8ByteLimitAndRejectsControlOrMalformedData() {
        assertTrue(LocalHotspotSafety.usableCredentials("万维", "12345678", 1))
        assertTrue(LocalHotspotSafety.usableCredentials("s".repeat(32), "12345678", 1))
        listOf(null, "", "  ", "<unknown ssid>", "s".repeat(33), "维".repeat(11), "a\nname", "a\u0000name", "a\u007fname", "\uD800").forEach {
            assertFalse(LocalHotspotSafety.usableCredentials(it, "12345678", 1))
        }
    }

    @Test fun passwordMustRemainUsablePrintableAsciiWithinPersonalSecurityLimits() {
        assertTrue(LocalHotspotSafety.usableCredentials("Wanwei", "x".repeat(63), 1))
        assertTrue(LocalHotspotSafety.usableCredentials("Wanwei", "pass word", 2))
        listOf("x".repeat(7), "x".repeat(64), "1234567\n", "1234567\u0000", "1234567中", "1234567\u007f").forEach {
            assertFalse(LocalHotspotSafety.usableCredentials("Wanwei", it, 3))
        }
    }

    @Test fun onlyMatchingGenerationBeforeSetupDeadlineCanBecomeActive() {
        val run = LocalHotspotSafety.Lifecycle(7, 100)
        assertEquals(LocalHotspotSafety.StartedAction.CLOSE_STALE_RESERVATION, run.started(6, 101, true))
        assertEquals(LocalHotspotSafety.Phase.PENDING, run.phase)
        assertEquals(null, run.activeDeadline)
        assertEquals(LocalHotspotSafety.StartedAction.ACTIVATE, run.started(7, 15_099, true))
        assertEquals(135_099L, run.activeDeadline)
        assertFalse(run.activeExpired(135_098))
        assertTrue(run.activeExpired(135_099))
    }

    @Test fun duplicatesCannotExtendTheActiveWindowOrActAsAnotherActivation() {
        val run = LocalHotspotSafety.Lifecycle(7, 100)
        assertEquals(LocalHotspotSafety.StartedAction.ACTIVATE, run.started(7, 101, true))
        assertEquals(LocalHotspotSafety.StartedAction.CLOSE_OWN_RESERVATION, run.started(7, 1_000, true))
        assertEquals(120_101L, run.activeDeadline)
    }

    @Test fun exactSetupDeadlineAndClockRollbackCloseInsteadOfActivatingAndRemainLocked() {
        listOf(99L, 15_100L).forEach { at ->
            val run = LocalHotspotSafety.Lifecycle(7, 100)
            assertEquals(LocalHotspotSafety.StartedAction.CLOSE_OWN_RESERVATION, run.started(7, at, true))
            assertTrue(run.uncertain)
            assertEquals(null, run.activeDeadline)
            assertTrue(run.released(7, true))
            assertTrue(run.uncertain)
            assertTrue(run.ownRequestReleased)
        }
    }

    @Test fun pendingCancelOrTimeoutCannotBeUnlockedByLateFailureOrSuccessfulLateClose() {
        val run = LocalHotspotSafety.Lifecycle(7, 100)
        assertTrue(run.stopPending())
        assertTrue(run.uncertain)
        assertEquals(LocalHotspotSafety.StartedAction.CLOSE_OWN_RESERVATION, run.started(7, 102, true))
        assertEquals(null, run.activeDeadline)
        assertTrue(run.released(7, true))
        assertTrue(run.failed(7))
        assertTrue(run.ownRequestReleased)
        assertTrue(run.uncertain)
        assertFalse(run.stopPending())
    }

    @Test fun invalidStartedConfigurationOnlyPermitsClosingTheCapturedReservation() {
        val run = LocalHotspotSafety.Lifecycle(7, 100)
        assertEquals(LocalHotspotSafety.StartedAction.CLOSE_OWN_RESERVATION, run.started(7, 101, false))
        assertEquals(LocalHotspotSafety.Phase.RELEASING, run.phase)
        assertEquals(null, run.activeDeadline)
        assertTrue(run.released(7, true))
        assertTrue(run.ownRequestReleased)
        assertFalse(run.uncertain)
        assertEquals("unknown_not_observable_via_public_api", LocalHotspotSafety.GLOBAL_AP_ABSENCE)
    }

    @Test fun successfulCloseOnlyReleasesOwnRequestWithoutWaitingForStopped() {
        val run = LocalHotspotSafety.Lifecycle(7, 100)
        run.started(7, 101, true)
        assertTrue(run.released(7, true))
        assertEquals(LocalHotspotSafety.Phase.FINISHED, run.phase)
        assertTrue(run.ownRequestReleased)
        assertFalse(run.uncertain)
        assertFalse(run.failureObserved)
    }

    @Test fun closeFailureAndUnexpectedFailureAfterActivationKeepUncertaintySticky() {
        val releaseFailure = LocalHotspotSafety.Lifecycle(7, 100)
        releaseFailure.started(7, 101, true)
        releaseFailure.released(7, false)
        assertFalse(releaseFailure.ownRequestReleased)
        assertTrue(releaseFailure.uncertain)
        releaseFailure.released(7, true)
        assertTrue(releaseFailure.ownRequestReleased)
        assertTrue(releaseFailure.uncertain)
        val callbackFailure = LocalHotspotSafety.Lifecycle(8, 100)
        callbackFailure.started(8, 101, true)
        callbackFailure.failed(8)
        assertTrue(callbackFailure.uncertain)
    }

    @Test fun definitiveFailureBeforeAnyReservationDoesNotInventAReleaseOrLock() {
        val run = LocalHotspotSafety.Lifecycle(7, 100)
        assertTrue(run.failed(7))
        assertFalse(run.ownRequestReleased)
        assertFalse(run.uncertain)
        assertTrue(run.failureObserved)
        assertEquals(LocalHotspotSafety.Phase.FINISHED, run.phase)
    }

    @Test fun staleGenerationCannotReleaseOrFailTheCurrentLifecycle() {
        val run = LocalHotspotSafety.Lifecycle(8, 100)
        run.started(8, 101, true)
        assertFalse(run.released(7, true))
        assertFalse(run.failed(7))
        assertFalse(run.ownRequestReleased)
        assertFalse(run.failureObserved)
        assertEquals(LocalHotspotSafety.Phase.ACTIVE, run.phase)
        assertEquals(120_101L, run.activeDeadline)
    }
}
