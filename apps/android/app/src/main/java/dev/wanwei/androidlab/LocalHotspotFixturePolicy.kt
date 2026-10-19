package dev.wanwei.androidlab

/** Pure endpoint checks: a requested band or a DHCP gateway alone cannot identify an Android AP interface. */
internal object LocalHotspotFixturePolicy {
    data class Endpoint(val interfaceName: String, val address: String, val prefixLength: Int)
    val noncePattern = Regex("[0-9a-f]{32}")
    val pinPattern = Regex("[0-9a-f]{64}")
    /** This lab uses only short ASCII primitive fields, so lenient platform JSON syntax is unnecessary. */
    fun strictRequestShape(raw: String): Boolean {
        if (raw.length > 16_384) return false
        val keys = Regex("\"([A-Za-z][A-Za-z0-9]{0,31})\"\\s*:").findAll(raw).map { it.groupValues[1] }.toList()
        if (keys.size !in 1..10 || keys.toSet().size != keys.size) return false
        val field = "\"[A-Za-z][A-Za-z0-9]{0,31}\"\\s*:\\s*(?:\"[A-Za-z0-9_./:|\\-]{0,128}\"|true|false)"
        if (!Regex("\\s*\\{\\s*(?:$field(?:\\s*,\\s*$field)*)?\\s*\\}\\s*").matches(raw)) return false
        return true
    }
    private fun number(value: String): Long? {
        val parts = value.split('.')
        if (parts.size != 4 || parts.any { !it.matches(Regex("0|[1-9][0-9]{0,2}")) || it.toInt() > 255 }) return null
        return parts.fold(0L) { acc, part -> (acc shl 8) or part.toLong() }
    }
    fun privateIpv4(value: String): Boolean {
        val n = number(value) ?: return false
        return (n ushr 24) == 10L || (n ushr 20) == 0xac1L || (n ushr 16) == 0xc0a8L
    }
    fun validPeer(local: String, prefix: Int, peer: String): Boolean {
        if (prefix !in 1..30 || !privateIpv4(local) || !privateIpv4(peer) || local == peer) return false
        val l = number(local) ?: return false
        val p = number(peer) ?: return false
        val mask = (0xffffffffL shl (32 - prefix)) and 0xffffffffL
        val network = l and mask
        val broadcast = network or (mask xor 0xffffffffL)
        return (p and mask) == network && p != network && p != broadcast && l != network && l != broadcast
    }
    fun select(baseline: Set<Endpoint>, current: Set<Endpoint>, gateway: String, peer: String): Endpoint? {
        val candidates = current.filter { it !in baseline && privateIpv4(it.address) && it.prefixLength in 1..30 }
        // Ambiguous new IPv4 interfaces are never guessed from an interface-name convention.
        val only = candidates.singleOrNull() ?: return null
        return only.takeIf { it.address == gateway && validPeer(it.address, it.prefixLength, peer) }
    }
    fun stillPresent(expected: Endpoint, current: Set<Endpoint>): Boolean = expected in current
}
