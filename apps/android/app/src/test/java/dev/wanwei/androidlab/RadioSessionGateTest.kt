package dev.wanwei.androidlab

import org.junit.Assert.*
import org.junit.Test

class RadioSessionGateTest {
    @Test fun lateUncertaintyKeepsNewOwnerAndSurvivesItsNormalRelease() {
        val gate = RadioSessionGate()
        assertTrue(gate.acquire("old"))
        assertTrue(gate.release("old", false))
        assertTrue(gate.acquire("new"))
        gate.latchUncertain()
        assertTrue(gate.occupied())
        assertFalse(gate.release("old", false))
        assertTrue(gate.release("new", false))
        assertFalse(gate.occupied())
        assertTrue(gate.locked())
        assertFalse(gate.acquire("third"))
    }
}
