package dev.wanwei.androidlab

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.net.wifi.WpsInfo
import android.net.wifi.p2p.WifiP2pConfig
import android.net.wifi.p2p.WifiP2pDevice
import android.net.wifi.p2p.WifiP2pGroup
import android.net.wifi.p2p.WifiP2pInfo
import android.net.wifi.p2p.WifiP2pManager
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import org.json.JSONArray
import org.json.JSONObject
import java.net.NetworkInterface
import java.time.Instant
import java.util.Locale
import java.util.UUID

/** User-confirmed, bounded Direct experiment. Radio callbacks and JSON mutation run on the main looper. */
internal class DirectPairingSession(context: Context,
    private val anonymousToken: (String) -> String,
    private val changed: (String) -> Unit) {
    private val app = context.applicationContext
    private val ownerId = "pairing:${UUID.randomUUID()}"
    private val handler = Handler(Looper.getMainLooper())
    private val manager = app.getSystemService(Context.WIFI_P2P_SERVICE) as? WifiP2pManager
    private var channel: WifiP2pManager.Channel? = null
    private var receiverRegistered = false
    private var attempt = 0
    private var evidenceQuery = 0
    private var targetAddress: String? = null
    // Snapshot at start; the UI cannot alter a running negotiation's role preference.
    private var requestedGroupOwnerIntent = -1
    private var preflightClear = false
    private var connectIssued = false
    private var rediscoveryIssued = false
    private var rediscoveryAccepted = false
    private var rediscoveryStopConfirmed = false
    private var rediscoveryStopPending = false
    private var peerQueryPending = false
    private var accepted: Boolean? = null
    private var baselineGroupSignature: String? = null
    private var observer: WifiObservation? = null
    private var record: JSONObject? = null
    private var events = JSONArray()
    private var connectionSamples = JSONArray()
    private var startedMs = 0L
    private var stoppingAt = 0L
    private var cancelConfirmed = false
    private var cancelPending = false
    private var cancelRetryAllowed = false
    private var cancelRequestCount = 0
    private var removePending = false
    private var removeAccepted = false
    private var absenceSince = 0L
    private var connected = false
    var busy = false
        private set
    private var localRetryLocked = false
    val retryLocked: Boolean get() = localRetryLocked || radioGate.locked()
    val awaitingSystemNegotiation: Boolean get() = busy && connectIssued && !connected && stoppingAt == 0L
    val isStopping: Boolean get() = busy && stoppingAt != 0L
    private var phaseTimer: Runnable? = null
    private var tick: Runnable? = null
    private var cleanupTimer: Runnable? = null
    private var callbackTimer: Runnable? = null
    private val receiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            if (!busy) return
            when (intent.action) {
                WifiP2pManager.WIFI_P2P_CONNECTION_CHANGED_ACTION -> {
                    event("connection_changed_broadcast")
                    if (connectIssued) queryConnection()
                }
                WifiP2pManager.WIFI_P2P_PEERS_CHANGED_ACTION -> if (rediscoveryAccepted && !connectIssued && stoppingAt == 0L) requestFreshPeer()
                WifiP2pManager.WIFI_P2P_STATE_CHANGED_ACTION -> {
                    val state = intent.getIntExtra(WifiP2pManager.EXTRA_WIFI_STATE, -1)
                    event("p2p_state_changed").put("state", state)
                    if (state == WifiP2pManager.WIFI_P2P_STATE_DISABLED) cancel("radio_disabled")
                }
            }
        }
    }

    fun report(): JSONObject? {
        observer?.let { record?.put("wifiObservation", it.snapshot()) }
        return record
    }

    fun noteLifecycle(kind: String) { if (busy) { event(kind); notifyChange("系统确认期间继续等待，最迟 45 秒结束建链。") } }

    fun start(target: WifiP2pDevice, candidateToken: String, seenAtElapsedMs: Long,
              preferPhoneGroupOwner: Boolean = false) {
        if (busy || retryLocked || !radioGate.acquire(ownerId)) return
        busy = true; attempt++; evidenceQuery++
        startedMs = SystemClock.elapsedRealtime(); stoppingAt = 0L
        targetAddress = target.deviceAddress
        requestedGroupOwnerIntent = if (preferPhoneGroupOwner) 15 else -1
        preflightClear = false; connectIssued = false; accepted = null; connected = false
        rediscoveryIssued = false; rediscoveryAccepted = false; rediscoveryStopConfirmed = false; peerQueryPending = false
        rediscoveryStopPending = false
        cancelConfirmed = false; removePending = false; removeAccepted = false; absenceSince = 0L; baselineGroupSignature = null
        cancelPending = false; cancelRetryAllowed = false; cancelRequestCount = 0
        events = JSONArray()
        connectionSamples = JSONArray()
        record = JSONObject().put("id", UUID.randomUUID().toString()).put("state", "preflight")
            .put("startedAtUtc", Instant.now().toString()).put("selectedCandidateToken", candidateToken)
            .put("userConfirmed", true).put("candidateAgeMs", startedMs - seenAtElapsedMs)
            .put("connectRequestIssued", false).put("connectRequestAccepted", JSONObject.NULL)
            .put("actualConnectionObserved", false).put("applicationIdentityAuthenticated", false)
            .put("requestedGroupOwnerIntent", requestedGroupOwnerIntent)
            .put("rolePreferenceMode", if (preferPhoneGroupOwner) "prefer_phone_group_owner" else "platform_default")
            .put("rolePreferenceNotes", "The opt-in experiment requests maximum group owner intent. Negotiation may fail if the peer also requires the group owner role. This is not a 5 GHz request or Wi-Fi 7 MLO configuration. Only observed group role and frequency are evidence of the resulting connection.")
            .put("requestedNegotiationTimeoutMs", 45_000).put("connectedObservationWindowMs", 20_000)
            .put("cleanupTimeoutMs", 10_000).put("events", events).put("connectionSamples", connectionSamples)
            .put("notes", "Only an explicit candidate selection and confirmation can initiate connect. A connect acceptance callback is not a connection. System pairing is not application identity authentication. No file or socket traffic is implemented.")
        observer = WifiObservation(app, handler).also { it.start() }
        startTicks()
        val address = targetAddress
        if (address.isNullOrBlank() || address == "02:00:00:00:00:00" || startedMs - seenAtElapsedMs > 45_000 || target.status != WifiP2pDevice.AVAILABLE) {
            endWithoutRadio("candidate_invalid_or_expired", "候选已过期或系统报告它不可用，请重新扫描。")
            return
        }
        if (manager == null) { endWithoutRadio("service_unavailable", "Wi-Fi Direct 服务不可用。"); return }
        try {
            val thisAttempt = attempt
            channel = manager.initialize(app, Looper.getMainLooper()) {
                if (busy && attempt == thisAttempt) {
                    record?.put("channelDisconnected", true)
                    localRetryLocked = connectIssued || rediscoveryIssued
                    finish("channel_disconnected_cleanup_unconfirmed", "Direct 通道已断开，释放结果未知。请查看报告。")
                }
            }
            if (channel == null) { endWithoutRadio("channel_unavailable", "无法创建建链通道。"); return }
            val filter = IntentFilter().apply {
                addAction(WifiP2pManager.WIFI_P2P_CONNECTION_CHANGED_ACTION)
                addAction(WifiP2pManager.WIFI_P2P_PEERS_CHANGED_ACTION)
                addAction(WifiP2pManager.WIFI_P2P_STATE_CHANGED_ACTION)
            }
            if (Build.VERSION.SDK_INT >= 33) app.registerReceiver(receiver, filter, Context.RECEIVER_EXPORTED)
            else @Suppress("DEPRECATION") app.registerReceiver(receiver, filter)
            receiverRegistered = true
            checkPreflight(target)
        } catch (error: Exception) {
            record?.put("errorType", error.javaClass.simpleName)
            endWithoutRadio("preflight_exception", "建链检查失败：${error.javaClass.simpleName}。")
        }
    }

    private fun checkPreflight(target: WifiP2pDevice) {
        val thisAttempt = attempt
        val active = channel ?: return
        var groupReceived = false
        var infoReceived = false
        var group: WifiP2pGroup? = null
        var info: WifiP2pInfo? = null
        var decided = false
        val decide = {
            if (busy && attempt == thisAttempt && stoppingAt == 0L && groupReceived && infoReceived && !decided) {
                decided = true
                clearPhaseTimer()
                record?.put("preflightGroupInfoReceived", true)?.put("preflightConnectionInfoReceived", true)
                    ?.put("preExistingGroupPresent", group != null || info?.groupFormed == true)
                if (!PairingSafety.mayStart(group == null, info?.groupFormed == false, true, target.status == WifiP2pDevice.AVAILABLE, retryLocked)) {
                    endWithoutRadio("existing_group_or_unknown_refused", "系统已有 Direct 组或状态不明确，本次不接管也不删除，请先结束原连接。")
                } else {
                    preflightClear = true
                    record?.put("preflightNoExistingGroupVerified", true)
                    rediscoverSelectedPeer()
                }
            }
        }
        setPhaseTimer(3_000) {
            if (busy && attempt == thisAttempt && stoppingAt == 0L) endWithoutRadio("preflight_callback_timeout", "系统未及时返回已有连接状态，本次不发起配对。")
        }
        try {
            manager!!.requestGroupInfo(active) { value ->
                if (attempt == thisAttempt && busy && stoppingAt == 0L) { groupReceived = true; group = value; decide() }
            }
            manager.requestConnectionInfo(active) { value ->
                if (attempt == thisAttempt && busy && stoppingAt == 0L) { infoReceived = true; info = value; decide() }
            }
        } catch (error: SecurityException) {
            record?.put("errorType", error.javaClass.simpleName)
            endWithoutRadio("permission_denied", "附近设备权限不可用，本次不发起连接。")
        } catch (error: Exception) {
            record?.put("errorType", error.javaClass.simpleName)
            endWithoutRadio("preflight_exception", "无法核实已有组，本次不发起连接。")
        }
        if (busy) notifyChange("检查系统是否已有 Direct 连接……")
    }

    private fun rediscoverSelectedPeer() {
        val active = channel ?: return
        val thisAttempt = attempt
        // AOSP STOP_DISCOVERY flushes supplicant peers. The preceding bounded scan
        // has already stopped, so its cached address is a selection, not live evidence.
        // Rediscover that exact selection, then connect without an intervening stop.
        rediscoveryIssued = true
        record?.put("state", "rediscovering_selected_peer")?.put("rediscoveryWindowMs", 8_000)
        event("selected_peer_rediscovery_invoked")
        setPhaseTimer(8_000) {
            if (busy && attempt == thisAttempt && stoppingAt == 0L) cancel("selected_peer_rediscovery_timeout")
        }
        try {
            manager!!.discoverPeers(active, object : WifiP2pManager.ActionListener {
                override fun onSuccess() {
                    if (!busy || attempt != thisAttempt) return
                    rediscoveryAccepted = true
                    event("selected_peer_rediscovery_accepted")
                    if (stoppingAt == 0L) requestFreshPeer()
                }
                override fun onFailure(reason: Int) {
                    if (!busy || attempt != thisAttempt) return
                    // A negative action callback confirms discovery did not start.
                    rediscoveryStopConfirmed = true
                    record?.put("rediscoveryFailureCode", reason)
                    if (stoppingAt == 0L) cancel("selected_peer_rediscovery_rejected")
                }
            })
        } catch (error: SecurityException) {
            record?.put("errorType", error.javaClass.simpleName)
            cancel("permission_denied")
        } catch (error: Exception) {
            record?.put("errorType", error.javaClass.simpleName)
            cancel("rediscovery_exception")
        }
        if (busy && stoppingAt == 0L) notifyChange("重新确认已选候选仍在附近，最多 8 秒……")
    }

    private fun requestFreshPeer() {
        if (!busy || !rediscoveryAccepted || connectIssued || stoppingAt != 0L || peerQueryPending) return
        val active = channel ?: return
        val thisAttempt = attempt
        peerQueryPending = true
        try {
            manager!!.requestPeers(active) { list ->
                if (!busy || attempt != thisAttempt || stoppingAt != 0L) return@requestPeers
                peerQueryPending = false
                if (connectIssued) return@requestPeers
                val current = list.deviceList.firstOrNull { it.deviceAddress.equals(targetAddress, true) }
                record?.put("freshPeerListCount", list.deviceList.size)
                    ?.put("selectedTargetInFreshList", current != null)
                    ?.put("selectedTargetFreshStatus", current?.status ?: JSONObject.NULL)
                    ?.put("freshPeerListReadAtUtc", Instant.now().toString())
                event("fresh_peer_list_received").put("count", list.deviceList.size)
                    .put("targetFound", current != null).put("targetStatus", current?.status ?: JSONObject.NULL)
                if (current != null && current.status == WifiP2pDevice.AVAILABLE) {
                    clearPhaseTimer()
                    issueConnect(current)
                }
            }
        } catch (error: SecurityException) {
            peerQueryPending = false
            record?.put("errorType", error.javaClass.simpleName)
            cancel("permission_denied")
        } catch (error: Exception) {
            peerQueryPending = false
            record?.put("errorType", error.javaClass.simpleName)
            cancel("peer_refresh_exception")
        }
    }

    private fun issueConnect(device: WifiP2pDevice) {
        val active = channel ?: return
        val thisAttempt = attempt
        val config = WifiP2pConfig().apply {
            deviceAddress = device.deviceAddress
            wps.setup = WpsInfo.PBC
            // Leave the platform default (-1) untouched outside the opt-in experiment.
            if (requestedGroupOwnerIntent != -1) groupOwnerIntent = requestedGroupOwnerIntent
        }
        connectIssued = true
        record?.put("state", "connecting")?.put("connectRequestIssued", true)
        event("connect_invoked").put("requestedGroupOwnerIntent", requestedGroupOwnerIntent)
        setPhaseTimer(45_000) { if (busy && attempt == thisAttempt && stoppingAt == 0L) cancel("negotiation_timeout") }
        callbackTimer = Runnable {
            if (busy && attempt == thisAttempt && accepted == null && stoppingAt == 0L) cancel("connect_callback_timeout")
        }.also { handler.postDelayed(it, 5_000) }
        try {
            manager!!.connect(active, config, object : WifiP2pManager.ActionListener {
                override fun onSuccess() {
                    if (!busy || attempt != thisAttempt) return
                    accepted = true; callbackTimer?.let(handler::removeCallbacks)
                    record?.put("connectRequestAccepted", true)
                    event("connect_request_accepted")
                    if (stoppingAt != 0L) { record?.put("lateConnectAcceptanceDuringCleanup", true); queryConnection() }
                    else { notifyChange("已发起连接，请在电脑和系统提示中确认。等待实际连接，最长 45 秒。 "); queryConnection() }
                }
                override fun onFailure(reason: Int) {
                    if (!busy || attempt != thisAttempt) return
                    accepted = false; callbackTimer?.let(handler::removeCallbacks)
                    record?.put("connectRequestAccepted", false)?.put("platformFailureCode", reason)
                    event("connect_request_rejected").put("reason", reason)
                    if (stoppingAt == 0L) cancel("connect_request_rejected")
                }
            })
        } catch (error: SecurityException) {
            accepted = false
            record?.put("connectRequestAccepted", false)?.put("errorType", error.javaClass.simpleName)
            cancel("permission_denied")
        } catch (error: Exception) {
            accepted = false
            record?.put("connectRequestAccepted", false)?.put("errorType", error.javaClass.simpleName)
            cancel("connect_exception")
        }
        if (busy && stoppingAt == 0L) notifyChange("正在发起系统 Direct 连接，可能弹出确认窗口……")
    }

    private fun startTicks() {
        val thisAttempt = attempt
        var count = 0
        tick = object : Runnable {
            override fun run() {
                if (!busy || attempt != thisAttempt) return
                observer?.sample(if (stoppingAt != 0L) "cleanup" else if (connected) "connected" else "connecting")
                count++
                if (rediscoveryAccepted && !connectIssued && stoppingAt == 0L) requestFreshPeer()
                if (stoppingAt != 0L && cancelRetryAllowed) {
                    channel?.let { requestCancelConnect(it, thisAttempt) }
                }
                if (stoppingAt != 0L && rediscoveryIssued && !rediscoveryStopConfirmed && !rediscoveryStopPending) {
                    channel?.let { stopRediscovery(it, thisAttempt) }
                }
                if (connectIssued && count % 2 == 0) queryConnection()
                if (count % 5 == 0) notifyChange(null)
                handler.postDelayed(this, 1_000)
            }
        }.also { handler.postDelayed(it, 1_000) }
    }

    /** Two fresh callbacks are required. A broadcast payload alone never authorizes group removal. */
    private fun queryConnection() {
        if (!busy || !connectIssued || removePending) return
        val active = channel ?: return
        val thisAttempt = attempt
        val query = ++evidenceQuery
        var groupReceived = false
        var infoReceived = false
        var group: WifiP2pGroup? = null
        var info: WifiP2pInfo? = null
        var consumed = false
        val deadline = Runnable {
            if (busy && attempt == thisAttempt && query == evidenceQuery && !consumed) {
                consumed = true; event("connection_evidence_callback_timeout")
            }
        }
        val receive = {
            if (busy && attempt == thisAttempt && query == evidenceQuery && !consumed && groupReceived && infoReceived) {
                consumed = true; handler.removeCallbacks(deadline)
                record?.put("connection", connectionJson(info))?.put("group", groupJson(group))
                if (connectionSamples.length() < 80) connectionSamples.put(JSONObject()
                    .put("atUtc", Instant.now().toString()).put("elapsedMs", SystemClock.elapsedRealtime() - startedMs)
                    .put("connection", connectionJson(info)).put("group", groupJson(group)))
                else record?.put("connectionSampleLimitReached", true)
                if (stoppingAt != 0L) cleanupEvidence(group, info)
                else if (group != null && info?.groupFormed == true) established(group!!, info!!)
                else if (connected) cancel("connection_lost")
            }
        }
        handler.postDelayed(deadline, 1_500)
        try {
            manager!!.requestGroupInfo(active) { value -> groupReceived = true; group = value; receive() }
            manager.requestConnectionInfo(active) { value -> infoReceived = true; info = value; receive() }
        } catch (error: SecurityException) {
            consumed = true; handler.removeCallbacks(deadline)
            event("connection_evidence_permission_denied").put("errorType", error.javaClass.simpleName)
            if (stoppingAt == 0L) cancel("permission_denied")
        } catch (error: Exception) {
            consumed = true; handler.removeCallbacks(deadline)
            event("connection_evidence_exception").put("errorType", error.javaClass.simpleName)
            if (stoppingAt == 0L) cancel("connection_evidence_exception")
        }
    }

    private fun established(group: WifiP2pGroup, info: WifiP2pInfo) {
        if (!targetMatches(group)) { event("unexpected_group_preserved"); cancel("unexpected_group"); return }
        if (baselineGroupSignature != null && signature(group) != baselineGroupSignature) { cancel("group_changed"); return }
        val usable = group.`interface`?.isNotBlank() == true && info.groupOwnerAddress != null && interfaceAddresses(group.`interface`).length() > 0
        if (usable && record?.optBoolean("actualP2pEndpointAvailable") != true) {
            record?.put("actualP2pEndpointAvailable", true)?.put("endpointAvailableAtUtc", Instant.now().toString())
                ?.put("endpointGroup", JSONObject(groupJson(group).toString()))
        } else if (!connected) record?.put("actualP2pEndpointAvailable", false)
        if (!connected) {
            connected = true
            baselineGroupSignature = signature(group)
            clearPhaseTimer(); callbackTimer?.let(handler::removeCallbacks)
            record?.put("state", "connected_observing")?.put("actualConnectionObserved", true)
                ?.put("connectionObservedAtUtc", Instant.now().toString())?.put("selectedPeerInGroup", true)
                ?.put("establishedConnection", JSONObject(connectionJson(info).toString()))
                ?.put("establishedGroup", JSONObject(groupJson(group).toString()))
            event("actual_group_and_connection_observed")
            val thisAttempt = attempt
            setPhaseTimer(20_000) { if (busy && attempt == thisAttempt && stoppingAt == 0L) cancel("observation_window_completed") }
            notifyChange("实际 Direct 连接已建立。观察原 Wi-Fi 20 秒后自动结束；本版本尚不传文件。")
        }
    }

    fun cancel(reason: String = "cancelled_by_user") {
        if (!busy || stoppingAt != 0L) return
        clearPhaseTimer(); callbackTimer?.let(handler::removeCallbacks)
        stoppingAt = SystemClock.elapsedRealtime()
        record?.put("state", "stopping")?.put("finishReason", reason)?.put("cleanup", JSONObject().put("state", "checking"))
        event("cleanup_started").put("reason", reason)
        if (!connectIssued && !rediscoveryIssued) { finish("not_connected_no_radio_mutation", "本次尚未发起连接，已结束。"); return }
        val active = channel
        if (active == null) { localRetryLocked = true; finish("channel_unavailable", "通道不可用，释放未确认。"); return }
        val thisAttempt = attempt
        cleanupTimer = Runnable {
            if (busy && attempt == thisAttempt) {
                localRetryLocked = true
                finish("cleanup_timeout_unknown", "系统未确认释放。本次重试已锁定，请查看报告并结束系统 Direct 连接。")
            }
        }.also { handler.postDelayed(it, 10_000) }
        if (!connectIssued) {
            if (rediscoveryIssued && !rediscoveryStopConfirmed) stopRediscovery(active, thisAttempt)
            if (rediscoveryStopConfirmed) finish("rediscovery_stopped_no_connect", "未发起连接；本次重新发现已停止。请确认电脑仍在配对窗口内后重试。")
            return
        }
        requestCancelConnect(active, thisAttempt)
        queryConnection()
        notifyChange("正在取消协商并核实本次连接的释放，最多 10 秒……")
    }

    private fun requestCancelConnect(active: WifiP2pManager.Channel, thisAttempt: Int) {
        if (!busy || attempt != thisAttempt || stoppingAt == 0L || !connectIssued || cancelConfirmed || cancelPending) return
        if (cancelRequestCount > 0 && !cancelRetryAllowed) return
        // Reuse the original cleanup deadline. Only an explicit BUSY response
        // permits the one-second tick to retry; a missing callback is not BUSY.
        if (SystemClock.elapsedRealtime() - stoppingAt >= 10_000) return
        cancelPending = true
        cancelRetryAllowed = false
        val requestNumber = ++cancelRequestCount
        record?.optJSONObject("cleanup")?.put("cancelConnectRequestCount", requestNumber)
            ?.put("cancelConnectRetryCount", requestNumber - 1)
        event("cancel_connect_request_invoked").put("requestNumber", requestNumber)
        try {
            manager!!.cancelConnect(active, object : WifiP2pManager.ActionListener {
                override fun onSuccess() {
                    if (!busy || attempt != thisAttempt || stoppingAt == 0L || requestNumber != cancelRequestCount || !cancelPending) return
                    cancelPending = false
                    cancelConfirmed = true
                    record?.optJSONObject("cleanup")?.put("cancelConnectRequestResult", "accepted")
                    event("cancel_connect_request_accepted").put("requestNumber", requestNumber)
                    if (rediscoveryIssued && !rediscoveryStopConfirmed) stopRediscovery(active, thisAttempt)
                    queryConnection()
                }
                override fun onFailure(code: Int) {
                    if (!busy || attempt != thisAttempt || stoppingAt == 0L || requestNumber != cancelRequestCount || !cancelPending) return
                    cancelPending = false
                    cancelRetryAllowed = code == WifiP2pManager.BUSY
                    record?.optJSONObject("cleanup")?.put("cancelConnectRequestResult", "rejected")?.put("cancelFailureCode", code)
                    event("cancel_connect_request_rejected").put("reason", code)
                        .put("requestNumber", requestNumber).put("busyRetryAllowed", cancelRetryAllowed)
                    queryConnection()
                }
            })
        } catch (error: Exception) {
            cancelPending = false
            record?.optJSONObject("cleanup")?.put("cancelConnectRequestResult", "exception")?.put("errorType", error.javaClass.simpleName)
            event("cancel_connect_request_exception").put("requestNumber", requestNumber)
                .put("errorType", error.javaClass.simpleName)
        }
    }

    private fun stopRediscovery(active: WifiP2pManager.Channel, thisAttempt: Int) {
        if (!busy || attempt != thisAttempt || stoppingAt == 0L || rediscoveryStopConfirmed || rediscoveryStopPending) return
        rediscoveryStopPending = true
        try {
            manager!!.stopPeerDiscovery(active, object : WifiP2pManager.ActionListener {
                override fun onSuccess() {
                    if (!busy || attempt != thisAttempt) return
                    rediscoveryStopPending = false
                    rediscoveryStopConfirmed = true
                    record?.optJSONObject("cleanup")?.put("rediscoveryStopResult", "confirmed")
                    event("rediscovery_stop_confirmed")
                    if (!connectIssued) finish("rediscovery_stopped_no_connect", "未发起连接；本次重新发现已停止。请确认电脑仍在配对窗口内后重试。")
                    else queryConnection()
                }
                override fun onFailure(reason: Int) {
                    if (!busy || attempt != thisAttempt) return
                    record?.optJSONObject("cleanup")?.put("rediscoveryStopResult", "rejected")?.put("rediscoveryStopFailureCode", reason)
                    event("rediscovery_stop_rejected").put("reason", reason)
                    // The group-negotiation state can return BUSY while cancelConnect
                    // is still settling. Retry from the one-second tick, within the
                    // existing ten-second cleanup deadline; never overlap stop calls.
                    rediscoveryStopPending = reason != WifiP2pManager.BUSY
                    // Keep the deadline and radio gate; absence of a group is not a stop acknowledgment.
                }
            })
        } catch (error: Exception) {
            record?.optJSONObject("cleanup")?.put("rediscoveryStopResult", "exception")?.put("rediscoveryStopErrorType", error.javaClass.simpleName)
        }
    }

    private fun cleanupEvidence(group: WifiP2pGroup?, info: WifiP2pInfo?) {
        if (group == null && info?.groupFormed == false) {
            if (absenceSince == 0L) absenceSince = SystemClock.elapsedRealtime()
            val drained = SystemClock.elapsedRealtime() - absenceSince >= 2_000
            val mayFinish = drained && (!rediscoveryIssued || rediscoveryStopConfirmed) && (cancelConfirmed || accepted == false || record?.optJSONObject("cleanup")?.optString("removeGroupRequestResult") == "accepted")
            if (mayFinish) finish("no_group_observed_after_cancel", "本次已结束；系统回读未发现 Direct 组。可以重新扫描。")
            return
        }
        absenceSince = 0L
        if (group == null || info?.groupFormed != true) return
        val extra = hasOtherParticipants(group)
        val same = baselineGroupSignature == null || baselineGroupSignature == signature(group)
        val removable = PairingSafety.mayRemove(preflightClear, connectIssued, targetMatches(group), extra, same)
        record?.optJSONObject("cleanup")?.put("groupRemovalOwnershipGatePassed", removable)
        if (!removable) {
            localRetryLocked = true
            finish("unattributed_or_shared_group_preserved", "当前组无法归属于本次两端连接，已保留。请查看报告，结束原连接后重新打开应用。")
            return
        }
        baselineGroupSignature = signature(group)
        // An accepted removal is asynchronous. Observe until absent or the cleanup deadline;
        // repeating removeGroup while it settles can return BUSY and obscure success.
        if (removePending || removeAccepted) return
        removePending = true
        val thisAttempt = attempt
        event("remove_this_attempt_group_invoked")
        try {
            manager!!.removeGroup(channel!!, object : WifiP2pManager.ActionListener {
                override fun onSuccess() {
                    if (!busy || attempt != thisAttempt) return
                    removePending = false
                    removeAccepted = true
                    record?.optJSONObject("cleanup")?.put("removeGroupRequestResult", "accepted")
                    event("remove_group_request_accepted"); queryConnection()
                }
                override fun onFailure(code: Int) {
                    if (!busy || attempt != thisAttempt) return
                    removePending = false
                    record?.optJSONObject("cleanup")?.put("removeGroupRequestResult", "rejected")?.put("removeFailureCode", code)
                    localRetryLocked = true
                    finish("remove_group_rejected", "系统拒绝释放本次组，结果已记录；请先结束系统连接。")
                }
            })
        } catch (error: Exception) {
            localRetryLocked = true
            record?.optJSONObject("cleanup")?.put("removeGroupRequestResult", "exception")?.put("errorType", error.javaClass.simpleName)
            finish("remove_group_exception", "释放调用失败，结果已记录。")
        }
    }

    private fun targetMatches(group: WifiP2pGroup): Boolean = targetAddress?.let { target ->
        group.owner?.deviceAddress.equals(target, true) || group.clientList.any { it.deviceAddress.equals(target, true) }
    } ?: false

    private fun hasOtherParticipants(group: WifiP2pGroup): Boolean {
        // For a local GO, only the selected client is allowed. For a remote GO, a redacted
        // local client entry or zero clients is normal; multiple clients make ownership ambiguous.
        if (group.isGroupOwner) return group.clientList.any { !it.deviceAddress.equals(targetAddress, true) }
        return group.clientList.size > 1
    }

    private fun signature(group: WifiP2pGroup): String = listOf(group.`interface`.orEmpty(),
        group.owner?.deviceAddress.orEmpty().lowercase(Locale.ROOT),
        if (Build.VERSION.SDK_INT >= 30) group.networkId.toString() else "unknown").joinToString("|")

    private fun connectionJson(info: WifiP2pInfo?): JSONObject = JSONObject()
        .put("callbackReceived", true).put("groupFormed", info?.groupFormed ?: JSONObject.NULL)
        .put("isGroupOwner", info?.isGroupOwner ?: JSONObject.NULL)
        .put("groupOwnerAddress", info?.groupOwnerAddress?.hostAddress ?: JSONObject.NULL)

    private fun groupJson(group: WifiP2pGroup?): JSONObject {
        val value = JSONObject().put("callbackReceived", true).put("groupPresent", group != null)
        if (group == null) return value
        val iface = group.`interface`
        val addressRead = interfaceAddressObservation(iface)
        return value.put("interfaceName", iface ?: JSONObject.NULL).put("isGroupOwner", group.isGroupOwner)
            .put("frequencyMhz", if (Build.VERSION.SDK_INT >= 29) group.frequency else JSONObject.NULL)
            .put("groupOwnerToken", group.owner?.deviceAddress?.takeIf { it.isNotBlank() && it != "02:00:00:00:00:00" }?.let(anonymousToken) ?: JSONObject.NULL)
            .put("clientTokens", JSONArray(group.clientList.mapNotNull { it.deviceAddress?.takeIf { address -> address.isNotBlank() && address != "02:00:00:00:00:00" }?.let(anonymousToken) }))
            .put("clientCount", group.clientList.size).put("selectedPeerInGroup", targetMatches(group))
            .put("localInterfaceAddresses", addressRead.getJSONArray("addresses"))
            .put("interfaceAddressRead", addressRead).put("networkNameStored", false).put("passphraseRead", false)
    }

    private fun interfaceAddresses(name: String?): JSONArray = interfaceAddressObservation(name).getJSONArray("addresses")

    private fun interfaceAddressObservation(name: String?): JSONObject {
        val output = JSONArray()
        val result = JSONObject().put("addresses", output).put("atUtc", Instant.now().toString())
            .put("source", "NetworkInterface.getByName_for_actual_p2p_interface")
            .put("internetPermissionGranted", app.checkSelfPermission(android.Manifest.permission.INTERNET) == android.content.pm.PackageManager.PERMISSION_GRANTED)
        if (name.isNullOrBlank()) return result.put("status", "interface_name_missing")
        try {
            val iface = NetworkInterface.getByName(name) ?: return result.put("status", "interface_not_visible")
            iface.interfaceAddresses.forEach { address ->
                if (!address.address.isLoopbackAddress) output.put(JSONObject().put("address", address.address.hostAddress)
                    .put("prefixLength", address.networkPrefixLength.toInt()).put("isIpv6", address.address is java.net.Inet6Address))
            }
            result.put("status", if (output.length() > 0) "addresses_observed" else "no_addresses_observed")
        } catch (error: Exception) {
            record?.put("interfaceAddressErrorType", error.javaClass.simpleName)
            result.put("status", "read_failed").put("errorType", error.javaClass.simpleName)
        }
        return result
    }

    private fun endWithoutRadio(state: String, message: String) {
        record?.put("finishReason", state)
        finish("not_started", message)
    }
    private fun finish(cleanupState: String, message: String) {
        if (!busy) return
        busy = false; evidenceQuery++
        clearPhaseTimer()
        tick?.let(handler::removeCallbacks); cleanupTimer?.let(handler::removeCallbacks); callbackTimer?.let(handler::removeCallbacks)
        record?.put("state", "finished")?.put("finishedAtUtc", Instant.now().toString())
            ?.put("elapsedMs", SystemClock.elapsedRealtime() - startedMs)?.put("retryLocked", retryLocked)
        val cleanup = record?.optJSONObject("cleanup") ?: JSONObject().also { record?.put("cleanup", it) }
        cleanup.put("state", cleanupState).put("originalOrUnattributedGroupRemoved", false)
        observer?.let { record?.put("wifiObservation", it.stop()) }
        if (receiverRegistered) {
            runCatching { app.unregisterReceiver(receiver) }; receiverRegistered = false
        }
        if (Build.VERSION.SDK_INT >= 27) runCatching { channel?.close() }
        channel = null; targetAddress = null
        radioGate.release(ownerId, localRetryLocked)
        changed(message)
    }
    private fun event(kind: String): JSONObject {
        val item = JSONObject().put("kind", kind).put("atUtc", Instant.now().toString())
            .put("elapsedMs", SystemClock.elapsedRealtime() - startedMs)
        if (events.length() < 200) events.put(item) else record?.put("eventLimitReached", true)
        return item
    }
    private fun notifyChange(message: String?) { changed(message ?: "") }
    private fun setPhaseTimer(milliseconds: Long, action: () -> Unit) {
        clearPhaseTimer(); phaseTimer = Runnable(action).also { handler.postDelayed(it, milliseconds) }
    }
    private fun clearPhaseTimer() { phaseTimer?.let(handler::removeCallbacks); phaseTimer = null }

    companion object { internal val radioGate = RadioSessionGate() }
}
