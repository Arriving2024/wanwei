package dev.wanwei.androidlab

import android.content.Context
import android.content.pm.ApplicationInfo
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import org.json.JSONArray
import org.json.JSONObject
import java.net.Inet4Address
import java.net.NetworkInterface
import java.io.File
import java.io.FileOutputStream
import java.security.MessageDigest
import java.util.UUID

/** Owner-authorized, debug-only 1 MiB lab. USB carries parameters, never file bytes. */
internal class LocalHotspotFixtureLab(context: Context, private val sessionId: String,
    private val baseline: Set<LocalHotspotFixturePolicy.Endpoint>?, private val activeLease: () -> Boolean,
    private val changed: () -> Unit) {
    private val app = context.applicationContext
    private val handler = Handler(Looper.getMainLooper())
    @Volatile private var path: P0LocalHotspotPath? = null
    @Volatile private var stopped = false
    private var nonceHash: String? = null
    private var peerPin: String? = null
    private var configured = false
    private var transfer: P0TransferSession? = null
    private var tasks = 0
    private var taskRole: String? = null
    private var port: Int? = null
    private var taskState = "not_started"
    private val commands = mutableSetOf<String>()
    private val usedRoles = mutableSetOf<String>()
    private val outcomes = mutableMapOf<String, JSONObject>()
    private var stopWaiter: Runnable? = null
    private var stopResult: Boolean? = null
    private val stopCallbacks = mutableListOf<(Boolean) -> Unit>()
    private var statusPersistFailed = false

    fun endpointCandidates(): JSONArray = JSONArray().also { array ->
        endpoints().filter { baseline != null && it !in baseline && LocalHotspotFixturePolicy.privateIpv4(it.address) }
            .forEach { array.put(JSONObject().put("interfaceName", it.interfaceName).put("localAddress", it.address).put("prefixLength", it.prefixLength)) }
    }

    fun call(request: JSONObject): JSONObject {
        check(Looper.myLooper() == Looper.getMainLooper())
        if (!BuildConfig.DEBUG || (app.applicationInfo.flags and ApplicationInfo.FLAG_DEBUGGABLE) == 0)
            return failure("DEBUG_REQUIRED")
        if (request.optString("schema") != SCHEMA || request.optString("sessionId") != sessionId ||
            runCatching { UUID.fromString(sessionId).toString() != sessionId }.getOrDefault(true)) return failure("SESSION_MISMATCH")
        val action = request.optString("action")
        if (action == "configure") return configure(request)
        if (!keysExactly(request, setOf("schema", "sessionId", "nonce", "commandId", "action"))) return failure("INVALID_REQUEST")
        val presented = request.optString("nonce")
        if (!configured || !LocalHotspotFixturePolicy.noncePattern.matches(presented) ||
            nonceHash == null || !MessageDigest.isEqual(hash(presented).toByteArray(Charsets.US_ASCII), nonceHash!!.toByteArray(Charsets.US_ASCII))) return failure("NOT_CONFIGURED")
        val command = request.optString("commandId")
        if (!LocalHotspotFixturePolicy.noncePattern.matches(command) || commands.size >= 256 || !commands.add(command)) return failure("COMMAND_REPLAY_OR_LIMIT")
        return when (action) {
            "status" -> status()
            "cancel" -> { stop("CANCELLED") {}; status() }
            "receive", "send" -> start(action == "receive")
            else -> failure("INVALID_ACTION")
        }
    }

    private fun configure(request: JSONObject): JSONObject {
        if (!keysExactly(request, setOf("schema", "sessionId", "nonce", "peerPin", "fixture", "fixtureConsent", "localAddress", "peerAddress", "action", "commandId"))) return failure("INVALID_REQUEST")
        if (configured || stopped || !activeLease()) return failure("LEASE_NOT_CONFIGURABLE")
        val n = request.optString("nonce"); val pin = request.optString("peerPin")
        if (!LocalHotspotFixturePolicy.noncePattern.matches(n) || !LocalHotspotFixturePolicy.pinPattern.matches(pin) ||
            request.optString("fixture") != P0Protocol.FIXTURE || request.opt("fixtureConsent") != true) return failure("FIXTURE_CONSENT_REQUIRED")
        val command = request.optString("commandId")
        if (!LocalHotspotFixturePolicy.noncePattern.matches(command) || commands.size >= 256 || !commands.add(command)) return failure("COMMAND_REPLAY_OR_LIMIT")
        val before = baseline ?: return failure("BASELINE_INTERFACE_SNAPSHOT_UNAVAILABLE")
        val after = snapshotEndpoints() ?: return failure("CURRENT_INTERFACE_SNAPSHOT_UNAVAILABLE")
        val selected = LocalHotspotFixturePolicy.select(before, after, request.optString("localAddress"), request.optString("peerAddress"))
            ?: return failure("NEW_AP_INTERFACE_UNVERIFIED")
        nonceHash = hash(n); peerPin = pin
        path = P0LocalHotspotPath("local-wifi:$sessionId", selected.interfaceName, selected.address, selected.prefixLength,
            request.getString("peerAddress"), sessionId, hash("$sessionId|${selected.interfaceName}|${selected.address}|${selected.prefixLength}|${request.getString("peerAddress")}"), SystemClock.elapsedRealtime())
        configured = true
        publishStatus()
        return status()
    }

    private fun livePath(): P0LocalHotspotPath? {
        val expected = path ?: return null
        if (stopped || !activeLease() || !LocalHotspotFixturePolicy.stillPresent(
                LocalHotspotFixturePolicy.Endpoint(expected.interfaceName, expected.localAddress, expected.prefixLength), endpoints())) return null
        return expected.copy(observedAtElapsedMs = SystemClock.elapsedRealtime())
    }

    private fun start(receiver: Boolean): JSONObject {
        val role = if (receiver) "receiver" else "sender"
        if (stopped || livePath() == null) return failure("AP_PATH_UNVERIFIED")
        if (transfer?.busy == true || transfer?.retryLocked == true || tasks >= 2 || !usedRoles.add(role)) return failure("TASK_BUSY_OR_LIMIT")
        val identity = try { P0Identity() } catch (_: Exception) { usedRoles.remove(role); return failure("IDENTITY_UNAVAILABLE") }
        lateinit var created: P0TransferSession
        created = P0TransferSession(app, identity, ::livePath) { update ->
            handler.post {
                if (update.finished) created.report()?.let { outcomes[role] = it }
                if (transfer !== created) return@post
                taskState = update.state
                if (update.listenPort != null) port = update.listenPort
                publishStatus()
            }
        }
        transfer = created; taskRole = role; port = null; taskState = "starting"; tasks++
        created.startLocalHotspotFixture(peerPin!!, receiver, true)
        publishStatus()
        return status()
    }

    /** Async bounded stop: release the AP only after the socket/storage worker has settled. */
    fun stop(reason: String, settled: (Boolean) -> Unit) {
        check(Looper.myLooper() == Looper.getMainLooper())
        stopped = true; path = null
        transfer?.cancel(reason)
        publishStatus()
        val previous = stopResult
        if (previous != null) { settled(previous); return }
        stopCallbacks.add(settled)
        if (stopWaiter != null) return
        val deadline = SystemClock.elapsedRealtime() + 3_000
        val waiter = object : Runnable {
            override fun run() {
                val task = transfer
                val done = task?.busy != true
                if (done || SystemClock.elapsedRealtime() >= deadline) {
                    stopWaiter = null
                    peerPin = null
                    val finishedReport = task?.report()
                    val result = done && task?.retryLocked != true && (task == null ||
                        (finishedReport != null && finishedReport.optString("state") == "finished" && finishedReport.optBoolean("socketResourcesClosed") &&
                            !finishedReport.optBoolean("storageCleanupNeedsReview")))
                    stopResult = result
                    publishStatus()
                    val callbacks = stopCallbacks.toList(); stopCallbacks.clear()
                    callbacks.forEach { it(result) }
                } else handler.postDelayed(this, 50)
            }
        }
        stopWaiter = waiter
        handler.post(waiter)
    }

    fun status(): JSONObject {
        val latest = transfer?.report()
        if (latest?.optString("state") == "finished" && taskRole != null) outcomes[taskRole!!] = latest
        val summary = JSONObject().put("schema", SCHEMA).put("sessionId", sessionId)
            .put("configured", configured).put("stopped", stopped).put("state", taskState)
            .put("taskRole", taskRole ?: JSONObject.NULL).put("port", port ?: JSONObject.NULL)
            .put("taskFinished", latest?.optString("state") == "finished" && transfer?.busy == false)
            .put("taskBusy", transfer?.busy == true).put("taskCount", tasks)
            .put("baselineSnapshotCaptured", baseline != null).put("baselinePrivateIpv4Count", baseline?.size ?: JSONObject.NULL)
            .put("workerCleanupSettled", stopResult ?: JSONObject.NULL).put("statusReportPersistFailed", statusPersistFailed)
            .put("nonceSha256", nonceHash ?: JSONObject.NULL)
            .put("operatorFixturePreauthorized", configured).put("fixture", P0Protocol.FIXTURE)
            .put("endpointSource", "shell_assisted_native_dhcp_gateway_and_unique_new_android_ipv4_interface")
            .put("ordinaryProductFlowVerified", false).put("usbFileBytesUsed", false)
            .put("directPathVerified", false).put("mloVerified", false)
        if (latest != null) summary.put("transfer", safeTask(latest))
        val directions = JSONObject()
        outcomes.forEach { (role, report) -> directions.put(role, safeTask(report)) }
        return summary.put("directions", directions)
    }

    private fun safeTask(report: JSONObject): JSONObject = JSONObject().also { output ->
        listOf("id", "role", "state", "outcome", "errorCode", "bytesTransferred", "sha256", "savedLocally", "savedBasename",
            "peerSaveConfirmed", "socketResourcesClosed", "tlsProtocol", "tlsHandshakeCompleted", "applicationIdentityAuthenticated",
            "listenerPort", "fileSyncSucceeded", "renameSucceeded", "directorySyncSucceeded", "preauthorizationSource",
            "tcpRole", "tlsRole", "directPathVerified", "localHotspotPathVerified", "finishedAtUtc", "elapsedMs")
            .forEach { key -> if (report.has(key)) output.put(key, report.get(key)) }
    }
    private fun failure(code: String) = JSONObject().put("schema", SCHEMA).put("sessionId", sessionId).put("errorCode", code)
    private fun keysExactly(input: JSONObject, keys: Set<String>): Boolean = input.keys().asSequence().toSet() == keys
    private fun publishStatus() {
        val report = status()
        try {
            val directory = File(app.filesDir, "reports").apply { mkdirs() }
            val temporary = File(directory, "local-wifi-fixture-status.json.tmp")
            FileOutputStream(temporary).use { it.write(report.toString(2).toByteArray(Charsets.UTF_8)); it.fd.sync() }
            if (!temporary.renameTo(File(directory, "local-wifi-fixture-status.json"))) throw java.io.IOException("report_commit_failed")
        } catch (_: Exception) { statusPersistFailed = true }
        changed()
    }
    companion object {
        const val SCHEMA = "wanwei.local-wifi.fixture/1"
        private fun hash(value: String) = MessageDigest.getInstance("SHA-256").digest(value.toByteArray(Charsets.UTF_8))
            .joinToString("") { "%02x".format(it.toInt() and 255) }
        fun endpoints(): Set<LocalHotspotFixturePolicy.Endpoint> = snapshotEndpoints().orEmpty()
        fun snapshotEndpoints(): Set<LocalHotspotFixturePolicy.Endpoint>? = runCatching {
            NetworkInterface.getNetworkInterfaces().toList().filter { it.isUp && !it.isLoopback }.flatMap { network ->
                network.interfaceAddresses.mapNotNull { bound ->
                    val address = bound.address as? Inet4Address ?: return@mapNotNull null
                    val text = address.hostAddress ?: return@mapNotNull null
                    if (!LocalHotspotFixturePolicy.privateIpv4(text) || bound.networkPrefixLength.toInt() !in 1..30) null
                    else LocalHotspotFixturePolicy.Endpoint(network.name, text, bound.networkPrefixLength.toInt())
                }
            }.toSet()
        }.getOrNull()
    }
}
