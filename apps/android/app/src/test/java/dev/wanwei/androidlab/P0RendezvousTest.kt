package dev.wanwei.androidlab

import org.junit.Assert.*
import org.junit.Test
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.util.concurrent.CompletableFuture
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger

class P0RendezvousTest {
    private val sender = "a".repeat(64)
    private val receiver = "b".repeat(64)
    private val query = P0RendezvousProtocol.Message("QUERY", "c".repeat(32), sender, receiver)
    private val loopback = InetAddress.getByName("127.0.0.1") as Inet4Address
    private fun reject(bytes: ByteArray) {
        try { P0RendezvousProtocol.parse(bytes); fail("Invalid hint accepted") }
        catch (failure: P0Protocol.Failure) { assertEquals("INVALID_MESSAGE", failure.code) }
    }
    @Test fun canonicalQueryAndAnswerRoundTripAcrossSharedWireContract() {
        assertEquals(query, P0RendezvousProtocol.parse(P0RendezvousProtocol.encode(query)))
        val answer = query.copy(type = "ANSWER", port = 53127)
        assertEquals(answer, P0RendezvousProtocol.parse(P0RendezvousProtocol.encode(answer)))
        val canonical = "{\"protocol\":\"wanwei-rendezvous/0.1\",\"type\":\"ANSWER\",\"nonce\":\"${query.nonce}\",\"senderIdentity\":\"$sender\",\"receiverIdentity\":\"$receiver\",\"port\":53127}"
        assertArrayEquals(canonical.toByteArray(), P0RendezvousProtocol.encode(answer))
    }
    @Test fun unknownDuplicateEscapedAndMissingFieldsAreRejected() {
        val source = P0RendezvousProtocol.encode(query).toString(Charsets.UTF_8)
        reject(source.dropLast(1).plus(",\"path\":\"../other\"}").toByteArray())
        reject(source.dropLast(1).plus(",\"typ\\u0065\":\"QUERY\"}").toByteArray())
        reject(source.replace(",\"nonce\":\"${query.nonce}\"", "").toByteArray())
        reject(source.dropLast(1).plus(",\"port\":1}").toByteArray())
    }
    @Test fun portIsCanonicalIntegerAndNeverAPathOrAddress() {
        val answer = P0RendezvousProtocol.encode(query.copy(type = "ANSWER", port = 12345)).toString(Charsets.UTF_8)
        for (bad in listOf("0", "65536", "-1", "1.0", "1e2", "012345", "true", "null", "\"12345\"", "4294967296"))
            reject(answer.replace(":12345}", ":$bad}").toByteArray())
        assertEquals(65535, P0RendezvousProtocol.parse(P0RendezvousProtocol.encode(query.copy(type = "ANSWER", port = 65535))).port)
    }
    @Test fun malformedEncodingOversizeAndUnusableIdentityAreRejected() {
        reject(byteArrayOf(0xc0.toByte(), 0xaf.toByte()))
        reject(ByteArray(1025) { ' '.code.toByte() })
        val source = P0RendezvousProtocol.encode(query).toString(Charsets.UTF_8)
        reject(("\uFEFF" + source).toByteArray())
        reject(source.replace(sender, receiver).toByteArray())
        reject(source.replace(query.nonce, query.nonce.uppercase()).toByteArray())
        reject(source.replace("wanwei-rendezvous/0.1", "wanwei-rendezvous/0.2").toByteArray())
    }
    @Test fun hintFiltersBindBothPinsNonceAndDirectionButDoNotGrantFileConsent() {
        val answer = query.copy(type = "ANSWER", port = 42)
        assertTrue(P0RendezvousProtocol.queryMatches(query, receiver, sender))
        assertFalse(P0RendezvousProtocol.queryMatches(query, sender, receiver))
        assertTrue(P0RendezvousProtocol.answerMatches(answer, query))
        assertFalse(P0RendezvousProtocol.answerMatches(answer.copy(nonce = "d".repeat(32)), query))
        assertFalse(P0RendezvousProtocol.answerMatches(answer.copy(senderIdentity = receiver), query))
        assertFalse(P0RendezvousProtocol.answerMatches(query, query))
    }
    @Test fun realHostUdpResolvesDynamicPortAndClosesForImmediateReuse() {
        // Loopback tests the control transport only. It is not native or radio evidence.
        ServerSocket(0, 1, loopback).use { tcp ->
            val responder = P0Rendezvous.respond(loopback, loopback, receiver, sender, tcp.localPort) { }
            try {
                val hint = P0Rendezvous.resolve(loopback, loopback, sender, receiver) { }
                assertEquals(tcp.localPort, hint.port)
                assertTrue(hint.queriesSent in 1..20)
            } finally { assertTrue(responder.closeAndConfirm()) }
            assertEquals(1, responder.responsesSent)
            DatagramSocket(null).use { rebound -> rebound.reuseAddress = false; rebound.bind(InetSocketAddress(loopback, P0RendezvousProtocol.PORT)) }
        }
    }
    @Test fun staleNonceAndAnswerFromWrongSourcePortCannotSelectReceiver() {
        val ready = CompletableFuture<Unit>()
        val server = CompletableFuture.runAsync {
            DatagramSocket(null).use { socket ->
                socket.reuseAddress = false; socket.bind(InetSocketAddress(loopback, P0RendezvousProtocol.PORT)); socket.soTimeout = 2000
                ready.complete(Unit)
                val request = DatagramPacket(ByteArray(1025), 1025); socket.receive(request)
                val received = P0RendezvousProtocol.parse(request.data.copyOf(request.length))
                fun send(value: P0RendezvousProtocol.Message, from: DatagramSocket) {
                    val bytes = P0RendezvousProtocol.encode(value)
                    from.send(DatagramPacket(bytes, bytes.size, request.address, request.port))
                }
                send(received.copy(type = "ANSWER", nonce = "0".repeat(32), port = 1), socket)
                DatagramSocket(InetSocketAddress(loopback, 0)).use { wrong -> send(received.copy(type = "ANSWER", port = 2), wrong) }
                send(received.copy(type = "ANSWER", port = 53127), socket)
            }
        }
        ready.get(2, TimeUnit.SECONDS)
        assertEquals(53127, P0Rendezvous.resolve(loopback, loopback, sender, receiver) { }.port)
        server.get(3, TimeUnit.SECONDS)
    }
    @Test fun pathCancellationStopsLookupWithoutWaitingTenSeconds() {
        val checks = AtomicInteger()
        val start = System.nanoTime()
        DatagramSocket(InetSocketAddress(loopback, P0RendezvousProtocol.PORT)).use {
            try {
                P0Rendezvous.resolve(loopback, loopback, sender, receiver) {
                    if (checks.incrementAndGet() >= 3) P0Protocol.fail("DIRECT_PATH_UNVERIFIED")
                }
                fail("Lost path accepted")
            } catch (failure: P0Protocol.Failure) { assertEquals("DIRECT_PATH_UNVERIFIED", failure.code) }
        }
        assertTrue(TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - start) < 2000)
    }
    @Test fun actualLookupStopsAtDeadlineWithBoundedQueriesAndNoFallback() {
        val ready = CompletableFuture<Unit>()
        val monitor = CompletableFuture.supplyAsync {
            val arrivals = mutableListOf<Long>()
            DatagramSocket(InetSocketAddress(loopback, P0RendezvousProtocol.PORT)).use { socket ->
                socket.soTimeout = 1100; ready.complete(Unit)
                val end = System.nanoTime() + TimeUnit.SECONDS.toNanos(12)
                while (System.nanoTime() < end) {
                    val packet = DatagramPacket(ByteArray(1025), 1025)
                    try { socket.receive(packet) } catch (_: java.net.SocketTimeoutException) { break }
                    arrivals.add(System.nanoTime())
                    assertEquals("QUERY", P0RendezvousProtocol.parse(packet.data.copyOf(packet.length)).type)
                }
            }
            arrivals
        }
        ready.get(2, TimeUnit.SECONDS)
        val start = System.nanoTime()
        try { P0Rendezvous.resolve(loopback, loopback, sender, receiver) { }; fail("No answer must time out") }
        catch (failure: P0Protocol.Failure) { assertEquals("PORT_EXCHANGE_TIMEOUT", failure.code) }
        val elapsed = TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - start)
        assertTrue(elapsed in 9900..11500)
        val arrivals = monitor.get(3, TimeUnit.SECONDS)
        assertTrue(arrivals.size in 1..20)
        assertTrue(arrivals.zipWithNext().all { (first, second) -> TimeUnit.NANOSECONDS.toMillis(second - first) >= 450 })
    }
}
