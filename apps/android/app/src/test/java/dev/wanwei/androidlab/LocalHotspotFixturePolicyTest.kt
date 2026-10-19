package dev.wanwei.androidlab

import org.junit.Assert.*
import org.junit.Test

class LocalHotspotFixturePolicyTest {
    private fun endpoint(name: String, address: String, prefix: Int = 24) = LocalHotspotFixturePolicy.Endpoint(name, address, prefix)
    @Test fun rejectsGatewayOnAnExistingInterface() {
        val old = endpoint("wlan0", "192.168.4.1")
        assertNull(LocalHotspotFixturePolicy.select(setOf(old), setOf(old), old.address, "192.168.4.2"))
    }
    @Test fun requiresUniqueNewInterfaceAndExactNativeDhcpGateway() {
        val old = endpoint("wlan0", "192.168.1.10")
        val ap = endpoint("wlan1", "192.168.43.1")
        assertEquals(ap, LocalHotspotFixturePolicy.select(setOf(old), setOf(old, ap), ap.address, "192.168.43.2"))
        assertNull(LocalHotspotFixturePolicy.select(setOf(old), setOf(old, ap), "192.168.43.3", "192.168.43.2"))
        assertNull(LocalHotspotFixturePolicy.select(setOf(old), setOf(old, ap, endpoint("extra", "10.0.0.1")), ap.address, "192.168.43.2"))
    }
    @Test fun successfulEmptyBaselineDoesNotWeakenGatewayAndPeerChecks() {
        val ap = endpoint("wlan1", "192.168.43.1")
        assertEquals(ap, LocalHotspotFixturePolicy.select(emptySet(), setOf(ap), ap.address, "192.168.43.2"))
        assertNull(LocalHotspotFixturePolicy.select(emptySet(), setOf(ap), ap.address, "192.168.1.2"))
        assertNull(LocalHotspotFixturePolicy.select(emptySet(), emptySet(), ap.address, "192.168.43.2"))
    }
    @Test fun rejectsSelfNetworkBroadcastPublicAndNonCanonicalIps() {
        for (peer in listOf("192.168.43.1", "192.168.43.0", "192.168.43.255", "192.168.44.2", "127.0.0.1", "224.1.2.3", "8.8.8.8", "192.168.043.2", "host.invalid"))
            assertFalse(peer, LocalHotspotFixturePolicy.validPeer("192.168.43.1", 24, peer))
        assertFalse(LocalHotspotFixturePolicy.validPeer("192.168.43.1", 31, "192.168.43.2"))
        assertFalse(LocalHotspotFixturePolicy.validPeer("192.168.43.1", 0, "192.168.43.2"))
    }
    @Test fun rfc1918BoundariesAreExact() {
        listOf("10.0.0.1", "10.255.255.254", "172.16.0.1", "172.31.255.254", "192.168.0.1").forEach { assertTrue(it, LocalHotspotFixturePolicy.privateIpv4(it)) }
        listOf("9.255.255.255", "11.0.0.1", "172.15.255.255", "172.32.0.1", "192.167.0.1", "192.169.0.1").forEach { assertFalse(it, LocalHotspotFixturePolicy.privateIpv4(it)) }
    }
    @Test fun pathInvalidatesOnAddressPrefixOrInterfaceChange() {
        val ap = endpoint("wlan1", "192.168.43.1")
        assertTrue(LocalHotspotFixturePolicy.stillPresent(ap, setOf(ap)))
        assertFalse(LocalHotspotFixturePolicy.stillPresent(ap, setOf(endpoint("wlan2", ap.address))))
        assertFalse(LocalHotspotFixturePolicy.stillPresent(ap, setOf(endpoint("wlan1", ap.address, 16))))
        assertFalse(LocalHotspotFixturePolicy.stillPresent(ap, emptySet()))
    }
    @Test fun debugRequestRejectsDuplicatesNestedValuesTrailingAndLenientJson() {
        assertTrue(LocalHotspotFixturePolicy.strictRequestShape("""{"schema":"wanwei.local-wifi.fixture/1","fixtureConsent":true,"action":"configure"}"""))
        for (raw in listOf("""{"action":"send","action":"receive"}""", """{"action":{"action":"send"}}""",
            """{'action':'send'}""", """{action:"send"}""", """{"action":"send"}trailing""", """{"action":null}""",
            """{"action":"send","port":123}""", """{"action":"s\u0065nd"}"""))
            assertFalse(raw, LocalHotspotFixturePolicy.strictRequestShape(raw))
        assertFalse(LocalHotspotFixturePolicy.strictRequestShape(" ".repeat(16_385)))
    }
}
