package dev.wanwei.androidlab

/** A narrowly scoped debug operator entry; this is not the network transfer protocol. */
internal object P0UsbLabPolicy {
    const val SCHEMA = "wanwei.usb-fixture-lab/1"
    const val MAXIMUM_BYTES = 16_384
    val nonceFormat = Regex("[0-9a-f]{32}")
    data class Bootstrap(val nonce: String, val peerPin: String)
    data class Command(val nonce: String, val commandId: String, val action: String,
        val peerAddress: String? = null, val port: Int? = null)
    fun bootstrap(bytes: ByteArray, intentNonce: String, confirmedPeerPin: String): Bootstrap {
        val fields = P0Protocol.parseFlatObject(bytes, MAXIMUM_BYTES, true)
        if (fields.keys != setOf("schema", "sessionNonce", "peerPin", "fixtureConsent", "fixture")) P0Protocol.fail("INVALID_MESSAGE")
        val nonce = fields["sessionNonce"] as? String ?: P0Protocol.fail("INVALID_MESSAGE")
        val pin = fields["peerPin"] as? String ?: P0Protocol.fail("IDENTITY_MISMATCH")
        if (!nonceFormat.matches(nonce) || nonce != intentNonce) P0Protocol.fail("INVALID_MESSAGE")
        if (!P0Protocol.fingerprint.matches(pin) || pin != confirmedPeerPin) P0Protocol.fail("IDENTITY_MISMATCH")
        if (fields["schema"] != SCHEMA || fields["fixture"] != P0Protocol.FIXTURE || fields["fixtureConsent"] != true) P0Protocol.fail("INVALID_MESSAGE")
        return Bootstrap(nonce, pin)
    }
    fun command(bytes: ByteArray, activeNonce: String): Command {
        val fields = P0Protocol.parseFlatObject(bytes, MAXIMUM_BYTES)
        val action = fields["action"] as? String ?: P0Protocol.fail("INVALID_MESSAGE")
        val expected = setOf("sessionNonce", "commandId", "action") + if (action == "send") setOf("peerAddress", "port") else emptySet()
        if (fields.keys != expected || action !in setOf("receive", "send", "acceptfixedfixture", "cancel")) P0Protocol.fail("INVALID_MESSAGE")
        val nonce = fields["sessionNonce"] as? String ?: P0Protocol.fail("INVALID_MESSAGE")
        val commandId = fields["commandId"] as? String ?: P0Protocol.fail("INVALID_MESSAGE")
        if (!nonceFormat.matches(nonce) || nonce != activeNonce || !nonceFormat.matches(commandId)) P0Protocol.fail("INVALID_MESSAGE")
        if (action != "send") return Command(nonce, commandId, action)
        val port = fields["port"] as? Long ?: P0Protocol.fail("INVALID_MESSAGE")
        if (port !in 1..65535) P0Protocol.fail("INVALID_MESSAGE")
        val address = fields["peerAddress"] as? String ?: P0Protocol.fail("INVALID_MESSAGE")
        val parts = address.split('.')
        if (parts.size != 4 || parts.any { !it.matches(Regex("0|[1-9][0-9]{0,2}")) || it.toInt() > 255 }) P0Protocol.fail("DIRECT_PATH_UNVERIFIED")
        return Command(nonce, commandId, action, address, port.toInt())
    }
}
