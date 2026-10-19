package dev.wanwei.androidlab

import android.content.Context
import android.content.pm.ApplicationInfo
import android.net.wifi.SoftApConfiguration
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.util.SparseIntArray
import org.json.JSONArray
import org.json.JSONObject
import java.nio.ByteBuffer
import java.nio.charset.CodingErrorAction
import java.time.Instant
import java.util.UUID
import java.util.concurrent.Executor

/** A single 5 GHz local-only AP request. Never enables tethering, selects an uplink, or changes STA settings. */
internal class LocalHotspotSession(context: Context, private val changed: (String) -> Unit) {
    private val app = context.applicationContext
    // The callback/late-close lifecycle outlives the Activity and its own Handler cleanup.
    private val handler = Handler(Looper.getMainLooper())
    private val manager = app.getSystemService(Context.WIFI_SERVICE) as? WifiManager
    private val ownerId = "local-hotspot:${UUID.randomUUID()}"
    private var generation = 0L
    private var current: Run? = null
    private var localRetryLocked = false
    @Volatile private var fixtureLeaseId: String? = null
    @Volatile private var fixtureLeaseDeadline = 0L

    var busy = false
        private set
    val retryLocked: Boolean get() = localRetryLocked || DirectPairingSession.radioGate.locked()

    private class Run(val lifecycle: LocalHotspotSafety.Lifecycle, val baseline: Set<LocalHotspotFixturePolicy.Endpoint>?) {
        val id = UUID.randomUUID().toString()
        val events = JSONArray()
        val record = JSONObject().put("id", id).put("sessionId", id)
            .put("kind", "local_only_hotspot_single_5ghz_public_api")
            .put("state", "preparing").put("startedAtUtc", Instant.now().toString())
            .put("requestedBand", "5GHz").put("requestedChannel", "system_auto_select")
            .put("setupTimeoutMs", LocalHotspotSafety.SETUP_MS).put("activeTimeoutMs", LocalHotspotSafety.ACTIVE_MS)
            .put("requestIssued", false).put("requestCallbackPending", false)
            .put("localHotspotStartedCallbackObserved", false).put("validatedActiveReservationObserved", false)
            .put("actualFrequencyMhz", JSONObject.NULL).put("actualWifiStandard", JSONObject.NULL)
            .put("actualMloState", "unknown_requires_connected_peer_evidence")
            .put("bridgedApRequested", false).put("mloRequested", false)
            .put("wifiSettingsChangedByApp", false).put("originalWifiDisconnectionRequestedByApp", false)
            .put("originalWifiPreservationRequiredForThisExperiment", false)
            .put("internetSharingRequested", false).put("fileTransferStarted", false)
            .put("socketOpened", false).put("networkCredentialsStoredInReport", false)
            .put("rawSsidStored", false).put("rawBssidStored", false)
            .put("continuousInternetPreservationVerified", false).put("events", events)
            .put("cleanup", JSONObject().put("state", "not_released")
                .put("ownRequestReleased", false).put("globalApAbsence", LocalHotspotSafety.GLOBAL_AP_ABSENCE))
            .put("notes", "Single 5 GHz public local-only hotspot experiment. A callback and requested channels are not actual PHY, frequency, MLO, peer connectivity, or file-transfer evidence. No cellular tethering is requested. Closing the owned reservation releases only this app's request; the global AP may remain for other requestors.")
        var observer: WifiObservation? = null
        var reservation: WifiManager.LocalOnlyHotspotReservation? = null
        var ssid: String? = null // Only in memory; never passed to diagnostics or event text.
        var passphrase: CharArray? = null
        var securityType: Int? = null
        var requestIssued = false
        var callbackPending = false
        var finished = false
        var gateReleased = false
        var setupTimer: Runnable? = null
        var activeTimer: Runnable? = null
        var sampleTick: Runnable? = null
        var fixture: LocalHotspotFixtureLab? = null
        var fixtureSettled = false
        var fixtureClosing = false
    }

    fun report(): JSONObject? {
        current?.let { run ->
            try { run.observer?.let { run.record.put("wifiObservation", it.snapshot()) } }
            catch (error: Exception) { event(run, "observation_snapshot_exception").put("errorType", error.javaClass.simpleName) }
            run.fixture?.let {
                val fixture = it.status()
                run.record.put("fixedFileFixture", fixture).put("fileTransferStarted", fixture.optInt("taskCount") > 0)
                    .put("socketOpened", fixture.optJSONObject("transfer")?.has("listenerPort") == true)
            }
            return run.record
        }
        return null
    }

    /** Explicit debug-only handoff. The returned value must never be persisted or printed by callers. */
    fun debugCredentials(): JSONObject? {
        checkMainThread()
        val run = current ?: return null
        if (!BuildConfig.DEBUG || (app.applicationInfo.flags and ApplicationInfo.FLAG_DEBUGGABLE) == 0 ||
            !busy || run.finished || run.lifecycle.phase != LocalHotspotSafety.Phase.ACTIVE ||
            run.reservation == null || run.lifecycle.activeExpired(now())) return null
        val ssid = run.ssid ?: return null
        val passphrase = run.passphrase ?: return null
        val securityType = run.securityType ?: return null
        return JSONObject().put("ssid", ssid).put("passphrase", String(passphrase))
            .put("securityType", securityType).put("sessionId", run.id)
            .put("endpointCandidates", run.fixture?.endpointCandidates() ?: JSONArray())
    }

    fun debugCall(request: JSONObject): JSONObject {
        checkMainThread()
        if (!BuildConfig.DEBUG || (app.applicationInfo.flags and ApplicationInfo.FLAG_DEBUGGABLE) == 0)
            return JSONObject().put("errorCode", "DEBUG_REQUIRED")
        return current?.fixture?.call(request) ?: JSONObject().put("errorCode", "NO_FIXTURE_LEASE")
    }

    fun start() {
        checkMainThread()
        if (busy || retryLocked || !DirectPairingSession.radioGate.acquire(ownerId)) return
        generation++
        val run = Run(LocalHotspotSafety.Lifecycle(generation, now()), LocalHotspotFixtureLab.snapshotEndpoints())
        run.fixture = LocalHotspotFixtureLab(app, run.id, run.baseline,
            activeLease = { fixtureLeaseId == run.id && now() < fixtureLeaseDeadline },
            changed = { if (isCurrent(run)) { run.record.put("fixedFileFixture", run.fixture?.status()); notifyChange(run, "固定文件实验状态已更新；USB 仅交换参数，文件数据使用本次 Wi-Fi 连接。") } })
        current = run
        busy = true
        try {
            run.observer = WifiObservation(app, handler).also { it.start() }
            if (Build.VERSION.SDK_INT < 36) { finish(run, "not_started_requires_api_36"); return }
            val wifi = manager ?: kotlin.run { finish(run, "not_started_wifi_service_unavailable"); return }
            val channels = SparseIntArray().apply { put(SoftApConfiguration.BAND_5GHZ, 0) }
            val config = SoftApConfiguration.Builder().setChannels(channels).build()
            val callback = object : WifiManager.LocalOnlyHotspotCallback() {
                override fun onStarted(reservation: WifiManager.LocalOnlyHotspotReservation) = started(run, reservation)
                override fun onStopped() = stopped(run)
                override fun onFailed(reason: Int) = failed(run, reason)
            }
            run.setupTimer = Runnable {
                if (isCurrent(run) && !run.finished && run.callbackPending) {
                    event(run, "setup_timeout_no_public_pending_cancel")
                    run.lifecycle.stopPending()
                    finish(run, "pending_request_timeout_cleanup_unknown")
                }
            }.also { handler.postDelayed(it, maxOf(0, run.lifecycle.setupDeadline - now())) }
            // A caller may cancel only after start() returns on this same Looper. Set pending before entering the platform.
            run.requestIssued = true
            run.callbackPending = true
            run.record.put("requestIssued", true).put("requestCallbackPending", true).put("state", "pending")
            event(run, "public_local_only_hotspot_request_issued")
            wifi.startLocalOnlyHotspotWithConfiguration(config, Executor { task -> handler.post(task) }, callback)
            startSamples(run)
            notifyChange(run, "正在请求临时 5 GHz 本地连接；尚未确认实际频段、Wi-Fi 7 或文件传输。")
        } catch (error: SecurityException) {
            run.callbackPending = false
            run.lifecycle.failed(run.lifecycle.generation)
            event(run, "permission_rejected").put("errorType", error.javaClass.simpleName)
            finish(run, "not_started_permission_rejected")
        } catch (error: IllegalArgumentException) {
            run.callbackPending = false
            run.lifecycle.failed(run.lifecycle.generation)
            event(run, "configuration_rejected").put("errorType", error.javaClass.simpleName)
            finish(run, "not_started_configuration_rejected")
        } catch (error: Exception) {
            event(run, "request_exception").put("errorType", error.javaClass.simpleName)
            if (run.requestIssued) run.lifecycle.markUncertain()
            finish(run, if (run.requestIssued) "request_exception_cleanup_unknown" else "not_started_exception")
        }
    }

    fun cancel(reason: String = "user_cancelled") {
        checkMainThread()
        val run = current ?: return
        if (run.finished) return
        // Only caller-supplied, fixed reason tokens are retained, never arbitrary platform exception text.
        event(run, "cancel_requested").put("reason", reason.takeIf { it.matches(Regex("[a-z0-9_]{1,64}")) } ?: "other")
        val owned = run.reservation
        if (owned != null) {
            closeOwned(run, owned, "own_request_released")
        } else if (run.requestIssued && run.callbackPending) {
            run.lifecycle.stopPending()
            finish(run, "pending_stop_requested_cleanup_unknown")
        } else finish(run, "not_started_cancelled")
    }

    private fun started(run: Run, received: WifiManager.LocalOnlyHotspotReservation) {
        checkMainThread()
        event(run, "on_started")
        run.callbackPending = false
        run.setupTimer?.let(handler::removeCallbacks)
        run.setupTimer = null
        run.record.put("requestCallbackPending", false).put("localHotspotStartedCallbackObserved", true)
        if (Build.VERSION.SDK_INT < 36) {
            run.lifecycle.markUncertain()
            closeOwned(run, received, "unsupported_api_own_request_released")
            return
        }
        if (!isCurrent(run)) {
            run.lifecycle.markUncertain()
            localRetryLocked = true
            // This reservation belongs to the captured old request. Never read/export its credentials into the new run.
            closeOwned(run, received, "stale_own_reservation_released", preserveCurrentReservation = true)
            current?.takeIf { !it.finished }?.let { cancel("stale_callback") }
            return
        }
        if (run.finished) {
            // A timed-out request remains owned by this captured callback even after the UI has finished.
            // Never expose late credentials or turn a prior pending uncertainty into an active session.
            run.lifecycle.markUncertain()
            event(run, "late_started_reservation_will_be_closed")
            closeOwned(run, received, "own_request_released")
            return
        }
        if (run.reservation === received) { event(run, "duplicate_reservation_callback_ignored"); return }
        if (run.reservation != null) {
            run.lifecycle.markUncertain()
            closeOwned(run, received, "unexpected_duplicate_reservation_released", preserveCurrentReservation = true)
            run.reservation?.let { closeOwned(run, it, "own_request_released_after_duplicate") }
            return
        }
        val config: SoftApConfiguration
        val ssid: String?
        val passphrase: String?
        val securityType: Int
        try {
            config = received.softApConfiguration
            ssid = if (Build.VERSION.SDK_INT >= 33) config.wifiSsid?.bytes?.let { bytes ->
                Charsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT)
                    .onUnmappableCharacter(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(bytes)).toString()
            } else @Suppress("DEPRECATION") config.ssid
            passphrase = config.passphrase
            securityType = config.securityType
            // Returned channels remain configuration, not a measurement of the running frequency.
            if (Build.VERSION.SDK_INT >= 36) {
                val returned = config.channels
                val values = JSONArray()
                for (index in 0 until returned.size()) values.put(JSONObject().put("bandMask", returned.keyAt(index)).put("channel", returned.valueAt(index)))
                run.record.put("returnedConfigurationChannels", values)
            }
        } catch (error: Exception) {
            event(run, "configuration_read_exception").put("errorType", error.javaClass.simpleName)
            closeOwned(run, received, "invalid_configuration_own_request_released")
            return
        }
        val valid = LocalHotspotSafety.usableCredentials(ssid, passphrase, securityType)
        run.record.put("returnedConfigurationCredentialsValid", valid).put("returnedSecurityType", securityType)
        val disposition = run.lifecycle.started(run.lifecycle.generation, now(), valid)
        if (disposition != LocalHotspotSafety.StartedAction.ACTIVATE || run.finished) {
            event(run, if (run.finished) "late_started_reservation_will_be_closed" else "unusable_started_reservation_will_be_closed")
            closeOwned(run, received, "own_request_released")
            return
        }
        run.reservation = received
        run.ssid = ssid
        run.passphrase = passphrase!!.toCharArray()
        run.securityType = securityType
        fixtureLeaseDeadline = run.lifecycle.activeDeadline!!
        fixtureLeaseId = run.id
        run.record.put("state", "active").put("validatedActiveReservationObserved", true)
            .put("activeStartedAtUtc", Instant.now().toString()).put("activeWindowMs", LocalHotspotSafety.ACTIVE_MS)
        run.activeTimer = Runnable {
            if (isCurrent(run) && !run.finished && run.lifecycle.activeExpired(now())) cancel("active_window_expired")
        }.also { handler.postDelayed(it, maxOf(0, run.lifecycle.activeDeadline!! - now())) }
        notifyChange(run, "临时本地连接已就绪，最多保留 120 秒；实际频段、Wi-Fi 7 和 MLO 等待接收端核验。")
    }

    private fun stopped(run: Run) {
        checkMainThread()
        event(run, "on_stopped")
        run.callbackPending = false
        run.record.put("requestCallbackPending", false).put("platformStoppedCallbackObserved", true)
        if (!isCurrent(run) || run.finished) { updateLateReport(run); return }
        val held = run.reservation
        if (held != null) closeOwned(run, held, "own_request_released_after_platform_stop")
        else {
            run.lifecycle.failed(run.lifecycle.generation)
            finish(run, "platform_stopped_without_reservation")
        }
    }

    private fun failed(run: Run, reason: Int) {
        checkMainThread()
        event(run, "on_failed").put("reasonCode", reason)
        run.callbackPending = false
        run.record.put("requestCallbackPending", false).put("failureReasonCode", reason)
        run.lifecycle.failed(run.lifecycle.generation)
        if (!isCurrent(run) || run.finished) { updateLateReport(run); return }
        val held = run.reservation
        if (held != null) closeOwned(run, held, "own_request_released_after_failure")
        else finish(run, "platform_request_failed")
    }

    private fun closeOwned(run: Run, owned: WifiManager.LocalOnlyHotspotReservation, state: String,
                           preserveCurrentReservation: Boolean = false) {
        if (!preserveCurrentReservation && !run.fixtureSettled && run.fixture != null) {
            if (run.fixtureClosing) return
            run.fixtureClosing = true
            if (fixtureLeaseId == run.id) fixtureLeaseId = null
            run.fixture!!.stop("CANCELLED") { settled ->
                run.fixtureSettled = true
                run.record.put("fixedFileFixture", run.fixture!!.status())
                run.record.getJSONObject("cleanup").put("fixtureWorkerSettledBeforeApRelease", settled)
                if (!settled) { run.lifecycle.markUncertain(); localRetryLocked = true }
                closeOwned(run, owned, state, false)
            }
            return
        }
        var closeReturned = false
        try {
            owned.close()
            closeReturned = true
            event(run, "owned_reservation_close_returned")
        } catch (error: Exception) {
            event(run, "owned_reservation_close_exception").put("errorType", error.javaClass.simpleName)
        }
        run.lifecycle.released(run.lifecycle.generation, closeReturned)
        if (!preserveCurrentReservation) {
            run.reservation = null
            wipeCredentials(run)
        }
        run.record.getJSONObject("cleanup").put("state", if (closeReturned) state else "own_request_release_failed")
            .put("ownRequestReleased", run.lifecycle.ownRequestReleased)
            .put("globalApAbsence", LocalHotspotSafety.GLOBAL_AP_ABSENCE)
            .put("waitedForOnStoppedAfterClose", false)
        if (isCurrent(run) && !run.finished && !preserveCurrentReservation) finish(run, if (closeReturned) state else "own_request_release_failed")
        else updateLateReport(run)
    }

    private fun startSamples(run: Run) {
        run.sampleTick = object : Runnable {
            override fun run() {
                if (!isCurrent(run) || run.finished) return
                try { run.observer?.sample("local_hotspot_passive") }
                catch (error: Exception) { event(run, "wifi_sample_exception").put("errorType", error.javaClass.simpleName) }
                handler.postDelayed(this, 1_000)
            }
        }.also { handler.postDelayed(it, 1_000) }
    }

    private fun finish(run: Run, state: String) {
        if (run.finished) { updateLateReport(run); return }
        run.finished = true
        if (fixtureLeaseId == run.id) fixtureLeaseId = null
        run.setupTimer?.let(handler::removeCallbacks)
        run.activeTimer?.let(handler::removeCallbacks)
        run.sampleTick?.let(handler::removeCallbacks)
        run.setupTimer = null; run.activeTimer = null; run.sampleTick = null
        try { run.observer?.let { run.record.put("wifiObservation", it.stop()) } }
        catch (error: Exception) { event(run, "wifi_observation_stop_exception").put("errorType", error.javaClass.simpleName) }
        run.observer = null
        run.fixture?.let { run.record.put("fixedFileFixture", it.status()) }
        wipeCredentials(run)
        localRetryLocked = localRetryLocked || run.lifecycle.uncertain
        run.record.put("state", "finished").put("finishedAtUtc", Instant.now().toString())
            .put("elapsedMs", now() - run.lifecycle.startedAt).put("requestCallbackPending", run.callbackPending)
        run.record.getJSONObject("cleanup").put("state", state).put("ownRequestReleased", run.lifecycle.ownRequestReleased)
            .put("pendingRequestCancellationPublicApiAvailable", false)
            .put("pendingUncertaintyStickyUntilNextProcess", run.lifecycle.uncertain)
            .put("globalApAbsence", LocalHotspotSafety.GLOBAL_AP_ABSENCE)
        if (!run.gateReleased) {
            DirectPairingSession.radioGate.release(ownerId, localRetryLocked)
            run.gateReleased = true
        }
        if (isCurrent(run)) busy = false
        run.record.put("retryLocked", retryLocked)
        notifyChange(run, if (retryLocked) "本地连接实验已停止；请求或清理仍有不确定性，后续无线操作已锁定。"
            else if (run.lifecycle.ownRequestReleased) "已释放本次本地连接请求；系统热点是否完全关闭仍待独立核验。"
            else "本地连接未建立，结果已记录。")
    }

    private fun updateLateReport(run: Run) {
        localRetryLocked = localRetryLocked || run.lifecycle.uncertain
        // A contradictory callback may arrive after a definite failure already released the gate.
        // A recreated Activity may own the gate now. Latch uncertainty without touching its ownership.
        if (run.lifecycle.uncertain && run.finished) DirectPairingSession.radioGate.latchUncertain()
        run.record.put("requestCallbackPending", run.callbackPending).put("retryLocked", retryLocked)
        run.record.getJSONObject("cleanup").put("ownRequestReleased", run.lifecycle.ownRequestReleased)
            .put("pendingUncertaintyStickyUntilNextProcess", run.lifecycle.uncertain)
            .put("globalApAbsence", LocalHotspotSafety.GLOBAL_AP_ABSENCE)
        if (isCurrent(run)) notifyChange(run, "已记录迟到的系统回调；先前的不确定状态不会自动解除。")
    }

    private fun wipeCredentials(run: Run) {
        run.ssid = null
        run.passphrase?.fill('\u0000')
        run.passphrase = null
        run.securityType = null
    }

    private fun event(run: Run, kind: String): JSONObject = JSONObject().put("kind", kind)
        .put("atUtc", Instant.now().toString()).put("elapsedMs", now() - run.lifecycle.startedAt)
        .also { if (run.events.length() < 200) run.events.put(it) }

    private fun notifyChange(run: Run, message: String) {
        if (!isCurrent(run)) return
        try { changed(message) }
        catch (error: Exception) { event(run, "ui_callback_exception").put("errorType", error.javaClass.simpleName) }
    }

    private fun isCurrent(run: Run): Boolean = current === run && generation == run.lifecycle.generation
    private fun checkMainThread() = check(Looper.myLooper() == Looper.getMainLooper())
    private fun now(): Long = SystemClock.elapsedRealtime()
}
