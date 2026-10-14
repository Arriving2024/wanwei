package dev.wanwei.androidlab

import org.junit.Assert.*
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.EOFException
import java.io.InputStream
import java.security.MessageDigest

class P0ProtocolTest {
    private val pin = "a".repeat(64)
    private val id = "b".repeat(32)
    private fun control(source: String) = P0Protocol.parse(source.toByteArray(Charsets.UTF_8))
    private fun rejected(code: String = "INVALID_MESSAGE", action: () -> Unit) {
        try { action(); fail("Expected $code") } catch (failure: P0Protocol.Failure) { assertEquals(code, failure.code) }
    }
    @Test fun fixtureBytesProduceContractHash() {
        val bytes = ByteArray(P0Protocol.SIZE.toInt()) { (it % 256).toByte() }
        assertEquals(P0Protocol.HASH, MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it.toInt() and 255) })
    }
    @Test fun allControlMessagesRoundTripInCanonicalForm() {
        val messages = listOf(
            P0Protocol.json("HELLO", "version" to "0.1", "role" to "sender", "service" to "file.p0", "identity" to pin),
            P0Protocol.json("OFFER", "transferId" to id, "fixture" to P0Protocol.FIXTURE, "sizeBytes" to P0Protocol.SIZE, "sha256" to P0Protocol.HASH),
            P0Protocol.json("ACCEPT", "transferId" to id),
            P0Protocol.json("REJECT", "transferId" to id, "reason" to "USER_DECLINED"),
            P0Protocol.json("COMPLETE", "transferId" to id, "sizeBytes" to P0Protocol.SIZE, "sha256" to P0Protocol.HASH),
            P0Protocol.json("ERROR", "transferId" to null, "code" to "INVALID_MESSAGE"))
        assertEquals(listOf("HELLO", "OFFER", "ACCEPT", "REJECT", "COMPLETE", "ERROR"), messages.map { P0Protocol.parse(it).type })
    }
    @Test fun duplicateEscapedKeysAndUnknownKeysAreRejected() {
        rejected { control("{\"type\":\"ACCEPT\",\"typ\\u0065\":\"ACCEPT\",\"transferId\":\"$id\"}") }
        rejected { control("{\"type\":\"ACCEPT\",\"transferId\":\"$id\",\"path\":\"../outside\"}") }
        rejected { control("{\"type\":\"ACCEPT\"}") }
    }
    @Test fun numbersCannotUseCoercionsFractionsExponentsSignsOrOverflow() {
        val template = "{\"type\":\"OFFER\",\"transferId\":\"$id\",\"fixture\":\"${P0Protocol.FIXTURE}\",\"sizeBytes\":TOKEN,\"sha256\":\"${P0Protocol.HASH}\"}"
        listOf("-0", "-1", "+1048576", "1.048576e6", "1048576.0", "01048576", "\"1048576\"", "true", "4294967296", "99999999999999999999999999", "null").forEach { token ->
            rejected { control(template.replace("TOKEN", token)) }
        }
    }
    @Test fun malformedUtf8BomRootArraysAndInvalidSurrogatesAreRejected() {
        rejected { P0Protocol.parse(byteArrayOf(0xc0.toByte(), 0xaf.toByte())) }
        rejected { control("\uFEFF{\"type\":\"ACCEPT\",\"transferId\":\"$id\"}") }
        rejected { control("[]") }
        rejected { control("{\"type\":\"ACCEPT\",\"transferId\":\"\\ud800\"}") }
        rejected { control("{\"type\":\"ACCEPT\",\"transferId\":\"$id\"} trailing") }
    }
    @Test fun unsupportedVersionAndServiceAreDistinct() {
        rejected("UNSUPPORTED_VERSION") { control("{\"type\":\"HELLO\",\"version\":\"0.2\",\"role\":\"sender\",\"service\":\"file.p0\",\"identity\":\"$pin\"}") }
        rejected("UNSUPPORTED_SERVICE") { control("{\"type\":\"HELLO\",\"version\":\"0.1\",\"role\":\"sender\",\"service\":\"media.screen\",\"identity\":\"$pin\"}") }
    }
    @Test fun partialAndCoalescedReadsPreserveTwoFrames() {
        val output = ByteArrayOutputStream()
        P0Protocol.write(output, 1, P0Protocol.json("ACCEPT", "transferId" to id))
        P0Protocol.write(output, 2, byteArrayOf(0, 1, 2, 3))
        val partial = object : InputStream() {
            val input = ByteArrayInputStream(output.toByteArray())
            override fun read(): Int = input.read()
            override fun read(bytes: ByteArray, offset: Int, length: Int): Int = input.read(bytes, offset, minOf(length, 1))
        }
        assertEquals("ACCEPT", P0Protocol.expect(P0Protocol.read(partial), "ACCEPT", id).type)
        assertArrayEquals(byteArrayOf(0, 1, 2, 3), P0Protocol.read(partial).payload)
    }
    @Test fun headerBoundsAreCheckedBeforePayloadReadOrAllocation() {
        for (header in listOf(byteArrayOf(1, 0, 0, 0, 0), byteArrayOf(3, 0, 0, 0, 1), byteArrayOf(1, -1, -1, -1, -1), byteArrayOf(2, 0, 1, 0, 1))) {
            rejected("INVALID_FRAME") { P0Protocol.read(ByteArrayInputStream(header)) }
        }
        rejected("SIZE_MISMATCH") { P0Protocol.read(ByteArrayInputStream(byteArrayOf(2, 0, 0, 0, 2, 1, 2)), 1) }
    }
    @Test fun truncatedFrameFailsWithoutReturningData() {
        try { P0Protocol.read(ByteArrayInputStream(byteArrayOf(2, 0, 0, 0, 4, 1, 2))); fail("Expected EOF") }
        catch (_: EOFException) { }
    }
    @Test fun firstByteUsesPhaseBudgetAndHeaderDripDoesNotResetDeadline() {
        var time = 0L; val budgets = mutableListOf<Long>()
        val input = object : InputStream() {
            val bytes = ByteArrayInputStream(byteArrayOf(2, 0, 0, 0, 1, 9))
            override fun read(): Int = bytes.read()
            override fun read(bytes: ByteArray, offset: Int, length: Int): Int { time += 4_000; return this.bytes.read(bytes, offset, 1) }
        }
        rejected("TIMEOUT") { P0Protocol.read(input, clock = { time }, beforeRead = { budgets += it }) }
        assertEquals(Long.MAX_VALUE, budgets.first())
        assertEquals(listOf(10_000L, 6_000L, 2_000L), budgets.drop(1))
    }
    @Test fun frameStartHookCanRejectEarlyDataBeforeReadingPayload() {
        rejected("UNEXPECTED_MESSAGE") {
            P0Protocol.read(ByteArrayInputStream(byteArrayOf(2)), frameStarted = { if (it == 2) P0Protocol.fail("UNEXPECTED_MESSAGE") })
        }
    }
    @Test fun peerErrorIsTerminalAndDistinctFromLocallyGeneratedError() {
        val frame = P0Protocol.Frame(1, P0Protocol.json("ERROR", "transferId" to id, "code" to "CANCELLED"))
        try { P0Protocol.expect(frame, "COMPLETE", id); fail("Expected peer terminal") }
        catch (error: P0Protocol.PeerFailure) { assertEquals("CANCELLED", error.code) }
        rejected("UNEXPECTED_MESSAGE") { P0Protocol.expect(frame, "COMPLETE", "c".repeat(32)) }
    }
    @Test fun completeSizeAndHashMismatchHaveSpecificErrors() {
        rejected("SIZE_MISMATCH") { control("{\"type\":\"COMPLETE\",\"transferId\":\"$id\",\"sizeBytes\":1,\"sha256\":\"${P0Protocol.HASH}\"}") }
        rejected("HASH_MISMATCH") { control("{\"type\":\"COMPLETE\",\"transferId\":\"$id\",\"sizeBytes\":1048576,\"sha256\":\"${"0".repeat(64)}\"}") }
    }
}
