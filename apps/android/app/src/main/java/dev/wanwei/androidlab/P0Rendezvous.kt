package dev.wanwei.androidlab

import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetSocketAddress
import java.net.SocketTimeoutException
import java.security.SecureRandom
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

/** Unauthenticated hints only. Neither a packet nor matching pins establishes trust. */
internal object P0RendezvousProtocol {
    const val PROTOCOL = "wanwei-rendezvous/0.1"
    const val PORT = 45873
    const val MAX_BYTES = 1024
    data class Message(val type: String, val nonce: String, val senderIdentity: String,
        val receiverIdentity: String, val port: Int? = null)
    fun parse(bytes: ByteArray): Message {
        val values = P0Protocol.parseFlatObject(bytes, MAX_BYTES)
        val type = values["type"] as? String ?: P0Protocol.fail("INVALID_MESSAGE")
        val keys = setOf("protocol", "type", "nonce", "senderIdentity", "receiverIdentity") +
            if (type == "ANSWER") setOf("port") else emptySet()
        if (type !in setOf("QUERY", "ANSWER") || values.keys != keys || values["protocol"] != PROTOCOL)
            P0Protocol.fail("INVALID_MESSAGE")
        fun text(key: String) = values[key] as? String ?: P0Protocol.fail("INVALID_MESSAGE")
        val nonce = text("nonce"); val sender = text("senderIdentity"); val receiver = text("receiverIdentity")
        if (!P0Protocol.transferId.matches(nonce) || !P0Protocol.fingerprint.matches(sender) ||
            !P0Protocol.fingerprint.matches(receiver) || sender == receiver) P0Protocol.fail("INVALID_MESSAGE")
        val port = if (type == "ANSWER") {
            val number = values["port"] as? Long ?: P0Protocol.fail("INVALID_MESSAGE")
            if (number !in 1..65535) P0Protocol.fail("INVALID_MESSAGE")
            number.toInt()
        } else null
        return Message(type, nonce, sender, receiver, port)
    }
    fun encode(message: Message): ByteArray {
        val port = message.port?.let { ",\"port\":$it" }.orEmpty()
        val bytes = ("{\"protocol\":\"$PROTOCOL\",\"type\":\"${message.type}\",\"nonce\":\"${message.nonce}\"," +
            "\"senderIdentity\":\"${message.senderIdentity}\",\"receiverIdentity\":\"${message.receiverIdentity}\"$port}").toByteArray(Charsets.UTF_8)
        if (parse(bytes) != message) P0Protocol.fail("INVALID_MESSAGE")
        return bytes
    }
    fun queryMatches(message: Message, localPin: String, peerPin: String) = message.type == "QUERY" &&
        message.receiverIdentity == localPin && message.senderIdentity == peerPin
    fun answerMatches(message: Message, query: Message) = message.type == "ANSWER" && message.nonce == query.nonce &&
        message.senderIdentity == query.senderIdentity && message.receiverIdentity == query.receiverIdentity
    fun nonce(): String = ByteArray(16).also(SecureRandom()::nextBytes).joinToString("") { "%02x".format(it.toInt() and 255) }
}

/** Caller supplies verified native addresses and a live path check; no DNS, subnet search or broadcast. */
internal object P0Rendezvous {
    data class Hint(val port: Int, val queriesSent: Int)
    class Responder internal constructor(private val socket: DatagramSocket, private val worker: Thread,
        private val stop: AtomicBoolean, private val replies: AtomicInteger) {
        fun closeAndConfirm(): Boolean {
            stop.set(true); socket.close()
            if (Thread.currentThread() != worker) {
                try { worker.join(1000) }
                catch (_: InterruptedException) { Thread.currentThread().interrupt(); return false }
            }
            return socket.isClosed && !worker.isAlive
        }
        val responsesSent: Int get() = replies.get()
    }
    fun respond(local: Inet4Address, peer: Inet4Address, localPin: String, peerPin: String,
        tcpPort: Int, ensureCurrent: () -> Unit): Responder {
        require(tcpPort in 1..65535)
        val socket = bind(local, P0RendezvousProtocol.PORT)
        val stop = AtomicBoolean(false); val replies = AtomicInteger()
        val worker = Thread({
            val end = now() + 30_000; var lastReply = Long.MIN_VALUE / 2
            try {
                while (!stop.get() && now() < end && replies.get() < 64) {
                    ensureCurrent()
                    if (stop.get() || now() >= end) break
                    val packet = DatagramPacket(ByteArray(P0RendezvousProtocol.MAX_BYTES + 1), P0RendezvousProtocol.MAX_BYTES + 1)
                    try { socket.receive(packet) } catch (_: SocketTimeoutException) { continue }
                    if (stop.get() || now() >= end) break
                    if (packet.address != peer || packet.port !in 1..65535 || packet.length > P0RendezvousProtocol.MAX_BYTES) continue
                    val query = runCatching { P0RendezvousProtocol.parse(packet.data.copyOfRange(packet.offset, packet.offset + packet.length)) }.getOrNull() ?: continue
                    if (!P0RendezvousProtocol.queryMatches(query, localPin, peerPin) || now() - lastReply < 100) continue
                    ensureCurrent()
                    if (stop.get() || now() >= end) break
                    val bytes = P0RendezvousProtocol.encode(query.copy(type = "ANSWER", port = tcpPort))
                    socket.send(DatagramPacket(bytes, bytes.size, peer, packet.port))
                    lastReply = now(); replies.incrementAndGet()
                }
            } catch (_: Exception) {
                // The transport worker owns error reporting; hints never authorise a file.
            } finally { socket.close() }
        }, "wanwei-native-port-hint").apply { isDaemon = true }
        try { worker.start() } catch (error: Exception) { socket.close(); throw error }
        return Responder(socket, worker, stop, replies)
    }
    fun resolve(local: Inet4Address, peer: Inet4Address, localPin: String, peerPin: String,
        ensureCurrent: () -> Unit): Hint {
        val query = P0RendezvousProtocol.Message("QUERY", P0RendezvousProtocol.nonce(), localPin, peerPin)
        val bytes = P0RendezvousProtocol.encode(query)
        bind(local, 0).use { socket ->
            val end = now() + 10_000; var nextSend = 0L; var sent = 0
            while (now() < end) {
                ensureCurrent()
                if (now() >= end) break
                if (now() >= nextSend && sent < 20) {
                    socket.send(DatagramPacket(bytes, bytes.size, peer, P0RendezvousProtocol.PORT))
                    sent++; nextSend = now() + 500
                }
                val packet = DatagramPacket(ByteArray(P0RendezvousProtocol.MAX_BYTES + 1), P0RendezvousProtocol.MAX_BYTES + 1)
                try { socket.receive(packet) } catch (_: SocketTimeoutException) { continue }
                if (now() >= end) break
                if (packet.address != peer || packet.port != P0RendezvousProtocol.PORT || packet.length > P0RendezvousProtocol.MAX_BYTES) continue
                val answer = runCatching { P0RendezvousProtocol.parse(packet.data.copyOfRange(packet.offset, packet.offset + packet.length)) }.getOrNull() ?: continue
                if (!P0RendezvousProtocol.answerMatches(answer, query)) continue
                ensureCurrent()
                if (now() >= end) break
                return Hint(answer.port!!, sent)
            }
        }
        P0Protocol.fail("PORT_EXCHANGE_TIMEOUT")
    }
    private fun bind(local: Inet4Address, port: Int): DatagramSocket {
        require(!local.isAnyLocalAddress && !local.isMulticastAddress)
        val socket = DatagramSocket(null)
        try {
            socket.reuseAddress = false; socket.broadcast = false
            socket.bind(InetSocketAddress(local, port)); socket.soTimeout = 100
            check(socket.localAddress == local)
            return socket
        } catch (_: Exception) { socket.close(); P0Protocol.fail("PORT_EXCHANGE_UNAVAILABLE") }
    }
    private fun now() = System.nanoTime() / 1_000_000
}
