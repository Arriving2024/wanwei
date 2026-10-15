package dev.wanwei.androidlab

import android.content.Context
import android.net.ssl.SSLSockets
import android.os.Build
import android.system.Os
import android.system.OsConstants
import org.json.JSONObject
import java.io.File
import java.io.FileDescriptor
import java.io.FileOutputStream
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketTimeoutException
import java.security.MessageDigest
import java.security.SecureRandom
import java.time.Instant
import java.util.UUID
import java.util.concurrent.CompletableFuture
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference
import javax.net.ssl.SSLSocket

internal data class P0DirectPath(val evidenceId: String, val interfaceName: String, val localAddress: String,
    val prefixLength: Int, val groupOwnerAddress: String, val localIsOwner: Boolean, val signature: String,
    val observedAtElapsedMs: Long)
internal data class P0TransferUpdate(val state: String, val message: String, val listenPort: Int? = null,
    val offerPending: Boolean = false, val finished: Boolean = false)

/** Only the worker mutates its report. Main looper receives immutable snapshots and user decisions. */
internal class P0TransferSession(context: Context, private val identity: P0Identity,
    private val pathProvider: () -> P0DirectPath?, private val changed: (P0TransferUpdate) -> Unit) {
    private val app = context.applicationContext
    private val localPin = identity.fingerprint
    private val active = AtomicBoolean(false)
    private val cancelled = AtomicReference<String?>(null)
    private val socket = AtomicReference<Socket?>(null)
    private val listener = AtomicReference<ServerSocket?>(null)
    private enum class OfferConsent { USER_ACCEPTED, USER_DECLINED, OPERATOR_PREAUTHORIZED }
    private val offerDecision = AtomicReference<CompletableFuture<OfferConsent>?>(null)
    @Volatile private var phaseDeadline = 0L
    @Volatile private var absoluteDeadline = 0L
    val busy: Boolean get() = active.get()
    fun acceptOffer(accept: Boolean) { offerDecision.get()?.complete(if (accept) OfferConsent.USER_ACCEPTED else OfferConsent.USER_DECLINED) }
    fun acceptFixedFixtureByOperator(): Boolean = offerDecision.get()?.complete(OfferConsent.OPERATOR_PREAUTHORIZED) ?: false
    fun cancel(reason: String = "CANCELLED") {
        if (!active.get()) return
        cancelled.compareAndSet(null, reason)
        offerDecision.get()?.complete(OfferConsent.USER_DECLINED)
        runCatching { socket.get()?.close() }; runCatching { listener.get()?.close() }
    }
    fun startReceiver(peerPin: String, operatorPreauthorized: Boolean = false) = start("receiver", peerPin, null, null, operatorPreauthorized)
    fun startSender(peerPin: String, peerAddress: String, port: Int, operatorPreauthorized: Boolean = false) = start("sender", peerPin, peerAddress, port, operatorPreauthorized)

    private fun start(role: String, peerPin: String, peerAddress: String?, port: Int?, operatorPreauthorized: Boolean) {
        if (!active.compareAndSet(false, true)) return
        cancelled.set(null)
        val initialPath = pathProvider()
        val executor = Executors.newSingleThreadExecutor()
        executor.execute {
            val report = JSONObject().put("schema", "wanwei-android-transfer/0.1").put("appVersion", "0.3.1")
                .put("id", UUID.randomUUID().toString()).put("role", role).put("startedAtUtc", Instant.now().toString())
                .put("fixture", P0Protocol.FIXTURE).put("expectedSizeBytes", P0Protocol.SIZE).put("expectedSha256", P0Protocol.HASH)
                .put("localIdentity", localPin).put("confirmedPeerIdentity", peerPin)
                .put("trustMethod", "user_confirmed_full_spki_fingerprint_out_of_band")
                .put("applicationIdentityAuthenticated", false).put("tlsHandshakeCompleted", false)
                .put("savedLocally", false).put("peerSaveConfirmed", false).put("bytesTransferred", 0)
                .put("fileSyncSucceeded", false).put("renameSucceeded", false).put("directorySyncSucceeded", false)
                .put("operatorFixturePreauthorized", operatorPreauthorized).put("userAcceptedOffer", false)
                .put("preauthorizationSource", if (operatorPreauthorized) "debug_private_file_usb_lab" else JSONObject.NULL)
                .put("sessionResumptionPolicy", "fresh_SSLContext_once_session_tickets_disabled_no_early_application_data")
            var partial: String? = null
            var fileDescriptor: FileDescriptor? = null
            var partialIdentity: P0OwnedCommit.Identity? = null
            var finalPath: String? = null
            var finalIdentity: P0OwnedCommit.Identity? = null
            var reservationFd: FileDescriptor? = null
            var directoryFd: FileDescriptor? = null
            var directoryPath: String? = null
            var directoryIdentity: P0OwnedCommit.Identity? = null
            var taskId: String? = null
            var tls: SSLSocket? = null
            var authenticated = false
            var committed = false
            var lastMessage = "本次文件实验已结束。"
            var auxiliaryReader: Thread? = null
            val started = now()
            phaseDeadline = started + 30_000
            absoluteDeadline = started + 210_000 // TCP/TLS 30 seconds + authenticated session 180 seconds.
            val watchdog = Executors.newSingleThreadScheduledExecutor()
            watchdog.scheduleAtFixedRate({
                val failure = when {
                    !samePath(initialPath) -> "DIRECT_PATH_UNVERIFIED"
                    now() >= minOf(phaseDeadline, absoluteDeadline) -> "TIMEOUT"
                    else -> null
                }
                if (failure != null) cancel(failure)
            }, 0, 200, TimeUnit.MILLISECONDS)
            fun publish(state: String, message: String, portValue: Int? = null, pending: Boolean = false) {
                lastMessage = message
                report.put("state", state).put("updatedAtUtc", Instant.now().toString())
                persist(report)
                changed(P0TransferUpdate(state, message, portValue, pending))
            }
            fun phase(milliseconds: Long) { phaseDeadline = now() + milliseconds }
            fun storageStage(stage: String, operation: String) {
                report.put("storageStage", stage).put("storageOperation", operation)
            }
            fun read(remaining: Long = P0Protocol.SIZE, frameStarted: (Int) -> Unit = {}): P0Protocol.Frame {
                ensureCurrent(initialPath)
                return P0Protocol.read(tls!!.inputStream, remaining, frameStarted) { frameRemaining ->
                    ensureCurrent(initialPath)
                    // FIRST byte waits are interrupted by the phase watchdog; its deadline can extend
                    // as outgoing DATA progresses. SO_TIMEOUT itself cannot be refreshed mid-read.
                    val allowed = if (frameRemaining == Long.MAX_VALUE) absoluteDeadline - now()
                        else minOf(frameRemaining, phaseDeadline - now(), absoluteDeadline - now())
                    if (allowed <= 0) P0Protocol.fail("TIMEOUT")
                    tls!!.soTimeout = allowed.coerceAtMost(Int.MAX_VALUE.toLong()).toInt().coerceAtLeast(1)
                }
            }
            fun control(type: String, vararg fields: Pair<String, Any?>) {
                ensureCurrent(initialPath)
                P0Protocol.write(tls!!.outputStream, 1, P0Protocol.json(type, *fields))
            }
            fun checkHello(frame: P0Protocol.Frame, expectedRole: String) {
                val hello = P0Protocol.expect(frame, "HELLO")
                if (hello.string("identity") != peerPin) P0Protocol.fail("IDENTITY_MISMATCH")
                if (hello.string("role") != expectedRole) P0Protocol.fail("UNEXPECTED_MESSAGE")
            }
            try {
                if (Build.VERSION.SDK_INT < 29) P0Protocol.fail("INVALID_MESSAGE")
                if (!P0Protocol.fingerprint.matches(peerPin)) P0Protocol.fail("IDENTITY_MISMATCH")
                ensureCurrent(initialPath)
                val path = initialPath ?: P0Protocol.fail("DIRECT_PATH_UNVERIFIED")
                report.put("directEvidenceId", path.evidenceId).put("interfaceName", path.interfaceName)
                    .put("nativeLocalAddress", path.localAddress).put("nativeGroupOwnerAddress", path.groupOwnerAddress)
                    .put("directPathVerified", true).put("portExchange", "manual_out_of_band_lab_parameters")
                val local = ipv4(path.localAddress)
                val raw: Socket
                if (role == "receiver") {
                    val server = ServerSocket()
                    listener.set(server)
                    server.reuseAddress = false
                    server.bind(InetSocketAddress(local, 0), 1)
                    server.soTimeout = 30_000
                    report.put("listenerAddress", local.hostAddress).put("listenerPort", server.localPort)
                    publish("listening", "等待发送端：${local.hostAddress}:${server.localPort}。端口需通过线下实验参数交给对端。", server.localPort)
                    raw = server.accept(); socket.set(raw); server.close(); listener.set(null)
                    if (!peerWithinDirectSubnet(path, raw.inetAddress)) P0Protocol.fail("DIRECT_PATH_UNVERIFIED")
                } else {
                    if (port == null || port !in 1..65535) P0Protocol.fail("INVALID_MESSAGE")
                    val remote = ipv4(peerAddress ?: P0Protocol.fail("DIRECT_PATH_UNVERIFIED"))
                    if (!peerWithinDirectSubnet(path, remote)) P0Protocol.fail("DIRECT_PATH_UNVERIFIED")
                    raw = Socket(); socket.set(raw)
                    raw.bind(InetSocketAddress(local, 0))
                    raw.connect(InetSocketAddress(remote, port), (phaseDeadline - now()).coerceAtLeast(1).toInt())
                }
                if (raw.localAddress != local) P0Protocol.fail("DIRECT_PATH_UNVERIFIED")
                report.put("socketLocalAddress", raw.localAddress.hostAddress).put("socketLocalPort", raw.localPort)
                    .put("socketRemoteAddress", raw.inetAddress.hostAddress).put("socketRemotePort", raw.port)
                val context = identity.newContext(peerPin) // New context after accept: one peer, one handshake, one task.
                tls = context.socketFactory.createSocket(raw, raw.inetAddress.hostAddress, raw.port, true) as SSLSocket
                socket.set(tls)
                tls!!.useClientMode = role == "sender"
                tls!!.enabledProtocols = arrayOf("TLSv1.3")
                if (role == "receiver") tls!!.needClientAuth = true
                if (!SSLSockets.isSupportedSocket(tls!!)) P0Protocol.fail("INVALID_MESSAGE")
                SSLSockets.setUseSessionTickets(tls!!, false)
                tls!!.soTimeout = (phaseDeadline - now()).coerceAtLeast(1).toInt()
                tls!!.startHandshake()
                val certificate = tls!!.session.peerCertificates.single() as java.security.cert.X509Certificate
                P0Identity.validateCertificate(certificate, peerPin, if (role == "sender") "server" else "client")
                if (tls!!.session.protocol != "TLSv1.3") P0Protocol.fail("IDENTITY_MISMATCH")
                authenticated = true
                absoluteDeadline = now() + 180_000
                report.put("applicationIdentityAuthenticated", true).put("tlsHandshakeCompleted", true)
                    .put("authenticatedPeerIdentity", P0Identity.fingerprint(certificate)).put("tlsProtocol", tls!!.session.protocol)
                    .put("tlsCipherSuite", tls!!.session.cipherSuite).put("peerPublicKeyProof", "platform_TLS13_mutual_certificate_verify")
                    .put("sessionTicketsDisabledBeforeHandshake", true).put("freshTlsContextUsedOnce", true)
                phase(10_000)
                if (role == "sender") {
                    control("HELLO", "version" to "0.1", "role" to "sender", "service" to "file.p0", "identity" to localPin)
                    checkHello(read(), "receiver")
                    taskId = ByteArray(16).also { SecureRandom().nextBytes(it) }.joinToString("") { "%02x".format(it.toInt() and 255) }
                    report.put("transferId", taskId)
                    control("OFFER", "transferId" to taskId, "fixture" to P0Protocol.FIXTURE, "sizeBytes" to P0Protocol.SIZE, "sha256" to P0Protocol.HASH)
                    phase(120_000); publish("waiting_accept", "已认证对端，等待对方明确接受 1 MiB 测试数据。")
                    val response = read()
                    if (response.kind == 1) {
                        val parsed = P0Protocol.parse(response.payload)
                        if (parsed.type == "REJECT" && parsed.values["transferId"] == taskId) {
                            report.put("outcome", "rejected").put("rejectReason", parsed.string("reason"))
                            publish("rejected", "对方拒绝本次测试文件。")
                            return@execute
                        }
                    }
                    P0Protocol.expect(response, "ACCEPT", taskId)
                    phase(30_000); publish("sending", "正在发送并等待对方保存确认。")
                    // Read terminal/error while writing, so an early peer ERROR cancels blocked writes.
                    val terminal = CompletableFuture<P0Protocol.Control>()
                    val finalDataWriteStarted = AtomicBoolean(false)
                    val reader = Thread {
                        try {
                            var premature = false
                            val frame = read(frameStarted = { premature = !finalDataWriteStarted.get() })
                            val complete = P0Protocol.expect(frame, "COMPLETE", taskId)
                            if (premature) P0Protocol.fail("UNEXPECTED_MESSAGE")
                            terminal.complete(complete)
                        }
                        catch (error: Exception) { terminal.completeExceptionally(error); runCatching { tls?.close() } }
                    }.apply { isDaemon = true; name = "wanwei-p0-terminal-reader"; start() }
                    auxiliaryReader = reader
                    val chunk = ByteArray(65_536) { (it % 256).toByte() }
                    var bytes = 0L
                    while (bytes < P0Protocol.SIZE) {
                        ensureCurrent(initialPath)
                        if (terminal.isCompletedExceptionally) terminal.get()
                        if (bytes + chunk.size == P0Protocol.SIZE) finalDataWriteStarted.set(true)
                        P0Protocol.write(tls!!.outputStream, 2, chunk)
                        bytes += chunk.size; report.put("bytesTransferred", bytes); phase(30_000)
                    }
                    phase(30_000)
                    terminal.get(minOf(30_000L, absoluteDeadline - now()).coerceAtLeast(1), TimeUnit.MILLISECONDS)
                    reader.join(500)
                    report.put("peerSaveConfirmed", true).put("sha256", P0Protocol.HASH).put("outcome", "peer_saved_confirmed")
                    publish("complete", "对方已保存 1 MiB 测试文件，哈希校验通过。")
                } else {
                    checkHello(read(), "sender")
                    control("HELLO", "version" to "0.1", "role" to "receiver", "service" to "file.p0", "identity" to localPin)
                    val offer = P0Protocol.expect(read(), "OFFER")
                    taskId = offer.string("transferId"); report.put("transferId", taskId)
                    phase(120_000)
                    val decision = CompletableFuture<OfferConsent>(); offerDecision.set(decision)
                    val acceptWriteStarted = AtomicBoolean(false)
                    val firstData = CompletableFuture<P0Protocol.Frame>()
                    val firstReader = Thread {
                        try {
                            var premature = false
                            val frame = read(frameStarted = { kind ->
                                premature = !acceptWriteStarted.get()
                                if (kind == 2 && premature) P0Protocol.fail("UNEXPECTED_MESSAGE")
                            })
                            if (frame.kind != 2) { P0Protocol.expect(frame, "DATA", taskId); P0Protocol.fail("UNEXPECTED_MESSAGE") }
                            if (premature) P0Protocol.fail("UNEXPECTED_MESSAGE")
                            firstData.complete(frame)
                        } catch (error: Exception) {
                            firstData.completeExceptionally(error)
                            decision.completeExceptionally(error)
                            runCatching { tls?.close() }
                        }
                    }.apply { isDaemon = true; name = "wanwei-p0-offer-reader"; start() }
                    auxiliaryReader = firstReader
                    publish("offer_pending", if (operatorPreauthorized) "USB 辅助固定数据实验：已认证对端，本次 1 MiB 合成文件使用已授予的实验预授权。" else "已认证的对端请求发送 1 MiB 合成测试文件。请明确接受或拒绝。", pending = !operatorPreauthorized)
                    if (operatorPreauthorized) decision.complete(OfferConsent.OPERATOR_PREAUTHORIZED)
                    val accepted = decision.get(minOf(120_000L, absoluteDeadline - now()).coerceAtLeast(1), TimeUnit.MILLISECONDS)
                    offerDecision.set(null)
                    if (accepted == OfferConsent.USER_DECLINED) {
                        if (cancelled.get() != null) P0Protocol.fail(cancelled.get()!!)
                        control("REJECT", "transferId" to taskId, "reason" to "USER_DECLINED")
                        report.put("outcome", "rejected").put("userAcceptedOffer", false)
                        publish("rejected", "已拒绝本次测试文件。")
                        return@execute
                    }
                    report.put("userAcceptedOffer", accepted == OfferConsent.USER_ACCEPTED)
                        .put("operatorFixturePreauthorized", accepted == OfferConsent.OPERATOR_PREAUTHORIZED)
                        .put("preauthorizationSource", if (accepted == OfferConsent.OPERATOR_PREAUTHORIZED) "debug_private_file_usb_lab" else JSONObject.NULL)
                    val directory = File(app.filesDir, "p0-received")
                    storageStage("prepare_directory", "mkdir")
                    if (!directory.exists()) Os.mkdir(directory.path, 448)
                    storageStage("verify_directory", "lstat")
                    val initialDirectoryStat = Os.lstat(directory.path)
                    if (!OsConstants.S_ISDIR(initialDirectoryStat.st_mode) || initialDirectoryStat.st_uid != Os.getuid() ||
                        directory.canonicalFile.parentFile != app.filesDir.canonicalFile) P0Protocol.fail("STORAGE_FAILED")
                    storageStage("hold_directory", "open")
                    directoryFd = Os.open(directory.path, OsConstants.O_RDONLY or OsConstants.O_NOFOLLOW or OsConstants.O_CLOEXEC, 0)
                    storageStage("protect_directory", "fchmod")
                    Os.fchmod(directoryFd, 448)
                    storageStage("verify_held_directory", "fstat")
                    directoryIdentity = storageIdentity(Os.fstat(directoryFd))
                    if (directoryIdentity!!.key != storageIdentity(initialDirectoryStat).key || !directoryIdentity!!.directory ||
                        directoryIdentity!!.permissions != 448 || directoryIdentity!!.owner != Os.getuid()) P0Protocol.fail("STORAGE_FAILED")
                    directoryPath = directory.path
                    if (directory.usableSpace < P0Protocol.SIZE + 65_536) {
                        control("REJECT", "transferId" to taskId, "reason" to "NO_SPACE")
                        report.put("outcome", "rejected").put("rejectReason", "NO_SPACE")
                        return@execute
                    }
                    val name = UUID.randomUUID().toString()
                    val proposedPartial = File(directory, "$name.partial").path
                    storageStage("create_partial_exclusive", "open")
                    fileDescriptor = Os.open(proposedPartial, OsConstants.O_CREAT or OsConstants.O_EXCL or OsConstants.O_WRONLY or OsConstants.O_NOFOLLOW or OsConstants.O_CLOEXEC, 384)
                    partial = proposedPartial
                    storageStage("identify_partial", "fstat")
                    partialIdentity = storageIdentity(Os.fstat(fileDescriptor))
                    if (!partialIdentity!!.regular || partialIdentity!!.links != 1L || partialIdentity!!.owner != Os.getuid()) P0Protocol.fail("STORAGE_FAILED")
                    val proposedFinal = File(directory, "$name.bin").path
                    storageStage("reserve_final_exclusive", "open")
                    reservationFd = Os.open(proposedFinal, OsConstants.O_CREAT or OsConstants.O_EXCL or OsConstants.O_WRONLY or OsConstants.O_NOFOLLOW or OsConstants.O_CLOEXEC, 384)
                    finalPath = proposedFinal
                    storageStage("identify_final_reservation", "fstat")
                    finalIdentity = storageIdentity(Os.fstat(reservationFd))
                    if (!finalIdentity!!.regular || finalIdentity!!.links != 1L || finalIdentity!!.owner != Os.getuid() || finalIdentity!!.size != 0L) P0Protocol.fail("STORAGE_FAILED")
                    if (firstData.isCompletedExceptionally) firstData.get()
                    acceptWriteStarted.set(true)
                    control("ACCEPT", "transferId" to taskId)
                    phase(30_000); publish("receiving", "正在接收，完成后验证并保存到本机测试目录。")
                    val digest = MessageDigest.getInstance("SHA-256"); var bytes = 0L
                    while (bytes < P0Protocol.SIZE) {
                        val frame = if (bytes == 0L) firstData.get(minOf(30_000L, absoluteDeadline - now()).coerceAtLeast(1), TimeUnit.MILLISECONDS)
                            else read(P0Protocol.SIZE - bytes)
                        if (frame.kind != 2) { P0Protocol.expect(frame, "DATA", taskId); P0Protocol.fail("UNEXPECTED_MESSAGE") }
                        var offset = 0
                        while (offset < frame.payload.size) {
                            ensureCurrent(initialPath)
                            storageStage("write_partial", "write")
                            val written = Os.write(fileDescriptor, frame.payload, offset, frame.payload.size - offset)
                            if (written <= 0) P0Protocol.fail("STORAGE_FAILED")
                            offset += written
                        }
                        digest.update(frame.payload); bytes += frame.payload.size
                        report.put("bytesTransferred", bytes); phase(30_000)
                    }
                    val hash = digest.digest().joinToString("") { "%02x".format(it.toInt() and 255) }
                    firstReader.join(500)
                    report.put("sha256", hash)
                    if (hash != P0Protocol.HASH) P0Protocol.fail("HASH_MISMATCH")
                    storageStage("sync_partial_contents", "fsync")
                    Os.fsync(fileDescriptor)
                    report.put("fileSyncSucceeded", true)
                    ensureCurrent(initialPath)
                    storageStage("validate_owned_commit_paths", "lstat")
                    P0OwnedCommit.commit(directory.path, partial!!, finalPath!!, directoryIdentity!!,
                        partialIdentity!!, finalIdentity!!, observe = { storageIdentity(Os.lstat(it)) },
                        rename = { from, to ->
                            storageStage("rename_into_own_reservation", "rename")
                            Os.rename(from, to)
                        })
                    // Keep both FDs open through rename, so the reserved inode cannot be reused.
                    finalIdentity = partialIdentity; partial = null
                    report.put("renameSucceeded", true).put("commitMethod", "rename_into_exclusive_task_owned_reservation")
                    storageStage("sync_committed_directory", "fsync")
                    Os.fsync(directoryFd)
                    report.put("directorySyncSucceeded", true)
                    committed = true
                    report.put("savedLocally", true).put("savedBasename", File(finalPath!!).name)
                    storageStage("close_partial_descriptor", "close")
                    Os.close(fileDescriptor); fileDescriptor = null
                    storageStage("close_old_reservation_descriptor", "close")
                    Os.close(reservationFd); reservationFd = null
                    storageStage("commit_complete", "none")
                    control("COMPLETE", "transferId" to taskId, "sizeBytes" to P0Protocol.SIZE, "sha256" to hash)
                    report.put("completeSent", true).put("outcome", "saved_locally")
                    publish("complete", "本机已保存 1 MiB 测试文件，哈希校验通过。")
                }
            } catch (error: Exception) {
                val unwrapped = if (error is java.util.concurrent.ExecutionException) error.cause ?: error else error
                val code = cancelled.get() ?: when (unwrapped) {
                    is P0Protocol.Failure -> unwrapped.code
                    is P0Protocol.PeerFailure -> unwrapped.code
                    is SocketTimeoutException, is java.util.concurrent.TimeoutException -> "TIMEOUT"
                    is java.security.cert.CertificateException, is javax.net.ssl.SSLException -> "IDENTITY_MISMATCH"
                    is android.system.ErrnoException -> "STORAGE_FAILED"
                    else -> "INVALID_MESSAGE"
                }
                report.put("outcome", if (committed) "saved_locally_peer_confirmation_unknown" else "failed")
                    .put("errorCode", code).put("errorType", unwrapped.javaClass.simpleName)
                if (unwrapped is android.system.ErrnoException) report
                    .put("storageErrnoFunction", report.optString("storageOperation", "unknown"))
                    .put("storageErrnoNumber", unwrapped.errno)
                    .put("storageFailureStage", report.optString("storageStage", "unknown"))
                phaseDeadline = minOf(phaseDeadline, now() + 1_000)
                if (authenticated && unwrapped !is P0Protocol.PeerFailure && tls?.isClosed == false && code in P0Protocol.errors) runCatching {
                    P0Protocol.write(tls!!.outputStream, 1, P0Protocol.json("ERROR", "transferId" to taskId, "code" to code))
                }
                publish("failed", if (committed) "本机文件已提交，但对方是否收到保存确认未知。" else "本次传输结束：$code。")
            } finally {
                watchdog.shutdownNow()
                offerDecision.getAndSet(null)?.complete(OfferConsent.USER_DECLINED)
                if (!committed) {
                    val ownedDirectoryStillPresent = runCatching {
                        directoryPath != null && directoryIdentity != null &&
                            P0OwnedCommit.ownsDirectory(directoryIdentity!!, storageIdentity(Os.lstat(directoryPath!!)))
                    }.getOrDefault(false)
                    fun removeOwned(path: String?, expected: P0OwnedCommit.Identity?) {
                        if (path == null) return
                        if (!ownedDirectoryStillPresent || expected == null) { report.put("storageCleanupNeedsReview", true); return }
                        runCatching {
                            if (P0OwnedCommit.ownsFile(expected, storageIdentity(Os.lstat(path)))) Os.remove(path)
                            else report.put("foreignStoragePathPreserved", true)
                        }.onFailure { report.put("storageCleanupNeedsReview", true) }
                    }
                    removeOwned(partial, partialIdentity)
                    removeOwned(finalPath, finalIdentity)
                }
                runCatching { fileDescriptor?.let(Os::close) }
                runCatching { reservationFd?.let(Os::close) }
                runCatching { directoryFd?.let(Os::close) }
                runCatching { tls?.session?.invalidate() }
                val previousSocket = socket.getAndSet(null)
                val previousListener = listener.getAndSet(null)
                runCatching { previousSocket?.close() }; runCatching { previousListener?.close() }
                runCatching { auxiliaryReader?.join(500) }
                runCatching { watchdog.awaitTermination(500, TimeUnit.MILLISECONDS) }
                report.put("finishedAtUtc", Instant.now().toString())
                    .put("socketResourcesClosed", (previousSocket == null || previousSocket.isClosed) && (previousListener == null || previousListener.isClosed))
                    .put("elapsedMs", now() - started).put("state", "finished")
                try { persist(report) } finally {
                    active.set(false)
                    try { changed(P0TransferUpdate("finished", "$lastMessage 可交换发送和接收角色再试。", finished = true)) }
                    finally { executor.shutdown() }
                }
            }
        }
    }
    private fun samePath(expected: P0DirectPath?): Boolean {
        val actual = pathProvider() ?: return false
        if (expected == null || actual.signature != expected.signature || actual.localAddress != expected.localAddress || actual.interfaceName != expected.interfaceName || android.os.SystemClock.elapsedRealtime() - actual.observedAtElapsedMs > 5_000) return false
        return runCatching { NetworkInterface.getByName(actual.interfaceName)?.inetAddresses?.toList()?.any { it.hostAddress == actual.localAddress } == true }.getOrDefault(false)
    }
    private fun ensureCurrent(expected: P0DirectPath?) {
        cancelled.get()?.let(P0Protocol::fail)
        if (!samePath(expected)) P0Protocol.fail("DIRECT_PATH_UNVERIFIED")
        if (now() >= minOf(phaseDeadline, absoluteDeadline)) P0Protocol.fail("TIMEOUT")
    }
    private fun persist(report: JSONObject) {
        val directory = File(app.filesDir, "reports").apply { mkdirs() }
        val temporary = File(directory, "latest-transfer.json.tmp")
        try {
            FileOutputStream(temporary).use { it.write(report.toString(2).toByteArray(Charsets.UTF_8)); it.fd.sync() }
            if (!temporary.renameTo(File(directory, "latest-transfer.json"))) throw java.io.IOException("report_commit_failed")
        } catch (error: Exception) { report.put("reportWriteFailed", true).put("reportWriteErrorType", error.javaClass.simpleName) }
    }
    companion object {
        private fun storageIdentity(stat: android.system.StructStat) = P0OwnedCommit.Identity(
            "${stat.st_dev}:${stat.st_ino}", stat.st_uid, OsConstants.S_ISREG(stat.st_mode),
            OsConstants.S_ISDIR(stat.st_mode), stat.st_mode and 511, stat.st_nlink, stat.st_size)
        private fun now() = System.nanoTime() / 1_000_000
        private fun ipv4(value: String): Inet4Address {
            // Never resolve hostnames or accidentally select the ordinary network.
            val parts = value.split('.')
            if (parts.size != 4 || parts.any { !it.matches(Regex("0|[1-9][0-9]{0,2}")) || it.toInt() > 255 }) P0Protocol.fail("DIRECT_PATH_UNVERIFIED")
            return InetAddress.getByAddress(parts.map { it.toInt().toByte() }.toByteArray()) as Inet4Address
        }
        private fun peerWithinDirectSubnet(path: P0DirectPath, remote: InetAddress): Boolean {
            if (remote !is Inet4Address || remote.isAnyLocalAddress || remote.isLoopbackAddress || remote.isMulticastAddress || remote.hostAddress == path.localAddress) return false
            if (!path.localIsOwner) return remote.hostAddress == path.groupOwnerAddress
            if (path.prefixLength !in 1..30) return false
            fun number(address: InetAddress) = address.address.fold(0L) { acc, byte -> (acc shl 8) or (byte.toInt() and 255).toLong() }
            val mask = (0xffffffffL shl (32 - path.prefixLength)) and 0xffffffffL
            val localValue = number(ipv4(path.localAddress)); val remoteValue = number(remote)
            return (localValue and mask) == (remoteValue and mask) && (remoteValue and mask) != remoteValue && (remoteValue or mask) != 0xffffffffL
        }
    }
}
