package dev.wanwei.androidlab

import org.junit.Assert.*
import org.junit.Test

class P0UsbLabPolicyTest {
    private val nonce = "a".repeat(32)
    private val pin = "b".repeat(64)
    private val commandId = "c".repeat(32)
    private val bootstrap = "{\"schema\":\"wanwei.usb-fixture-lab/1\",\"sessionNonce\":\"$nonce\",\"peerPin\":\"$pin\",\"fixtureConsent\":true,\"fixture\":\"ramp256-1mib-v1\"}"
    private fun failure(code: String = "INVALID_MESSAGE", action: () -> Unit) {
        try { action(); fail("Expected $code") } catch (failure: P0Protocol.Failure) { assertEquals(code, failure.code) }
    }
    @Test fun bootstrapBindsExplicitNonceAlreadyConfirmedPinAndFixedFixture() {
        assertEquals(pin, P0UsbLabPolicy.bootstrap(bootstrap.toByteArray(), nonce, pin).peerPin)
        failure { P0UsbLabPolicy.bootstrap(bootstrap.toByteArray(), "d".repeat(32), pin) }
        failure("IDENTITY_MISMATCH") { P0UsbLabPolicy.bootstrap(bootstrap.toByteArray(), nonce, "e".repeat(64)) }
    }
    @Test fun bootstrapRejectsMissingFalseStringConsentAndExtraFields() {
        listOf(bootstrap.replace("true", "false"), bootstrap.replace("true", "\"true\""), bootstrap.replace(",\"fixtureConsent\":true", ""), bootstrap.dropLast(1) + ",\"path\":\"/outside\"}").forEach {
            failure { P0UsbLabPolicy.bootstrap(it.toByteArray(), nonce, pin) }
        }
    }
    @Test fun bootstrapRejectsDuplicateKeysAndDifferentFixture() {
        failure { P0UsbLabPolicy.bootstrap((bootstrap.dropLast(1) + ",\"fixtureConsent\":true}").toByteArray(), nonce, pin) }
        failure { P0UsbLabPolicy.bootstrap(bootstrap.replace("ramp256-1mib-v1", "userfile").toByteArray(), nonce, pin) }
    }
    @Test fun commandsAreBoundToNonceAndAnExactActionAllowlist() {
        for (action in listOf("receive", "acceptfixedfixture", "cancel")) {
            val source = "{\"sessionNonce\":\"$nonce\",\"commandId\":\"$commandId\",\"action\":\"$action\"}"
            assertEquals(action, P0UsbLabPolicy.command(source.toByteArray(), nonce).action)
            failure { P0UsbLabPolicy.command(source.toByteArray(), "d".repeat(32)) }
        }
        failure { P0UsbLabPolicy.command("{\"sessionNonce\":\"$nonce\",\"commandId\":\"$commandId\",\"action\":\"change_wifi\"}".toByteArray(), nonce) }
    }
    @Test fun sendNeedsCanonicalLiteralAddressAndBoundedIntegerPort() {
        val source = "{\"sessionNonce\":\"$nonce\",\"commandId\":\"$commandId\",\"action\":\"send\",\"peerAddress\":\"192.168.137.1\",\"port\":PORT}"
        assertEquals(54321, P0UsbLabPolicy.command(source.replace("PORT", "54321").toByteArray(), nonce).port)
        for (port in listOf("0", "65536", "-0", "1e4", "\"54321\"")) failure { P0UsbLabPolicy.command(source.replace("PORT", port).toByteArray(), nonce) }
        failure("DIRECT_PATH_UNVERIFIED") { P0UsbLabPolicy.command(source.replace("PORT", "54321").replace("192.168.137.1", "example.com").toByteArray(), nonce) }
    }
    @Test fun commandsCannotImportPathsAndSizeLimitPreventsUnboundedObjects() {
        val source = "{\"sessionNonce\":\"$nonce\",\"commandId\":\"$commandId\",\"action\":\"receive\",\"path\":\"../file\"}"
        failure { P0UsbLabPolicy.command(source.toByteArray(), nonce) }
        failure { P0UsbLabPolicy.command(ByteArray(16_385) { 32 }, nonce) }
    }
}
