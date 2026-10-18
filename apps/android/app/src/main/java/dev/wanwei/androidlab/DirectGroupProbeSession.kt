package dev.wanwei.androidlab

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.net.wifi.p2p.WifiP2pConfig
import android.net.wifi.p2p.WifiP2pGroup
import android.net.wifi.p2p.WifiP2pInfo
import android.net.wifi.p2p.WifiP2pManager
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import org.json.JSONArray
import org.json.JSONObject
import java.security.SecureRandom
import java.time.Instant
import java.util.Locale
import java.util.UUID

/** A bounded local 2.4 GHz GO probe. Never invites peers, opens ports, or changes STA settings. */
internal class DirectGroupProbeSession(context: Context,
    @Suppress("UNUSED_PARAMETER") anonymousToken: (String) -> String,
    private val changed: (String) -> Unit) {
    private val app = context.applicationContext
    private val handler = Handler(Looper.getMainLooper())
    private val manager = app.getSystemService(Context.WIFI_P2P_SERVICE) as? WifiP2pManager
    private val ownerId = "group-probe:${UUID.randomUUID()}"
    private var channel: WifiP2pManager.Channel? = null
    private var registered = false
    private var generation = 0
    private var queryId = 0
    private var queryPending = false
    private var previousQueryCompletedAt: Long? = null
    private var record: JSONObject? = null
    private var events = JSONArray()
    private var samples = JSONArray()
    private var observer: WifiObservation? = null
    private var windows: GroupProbeSafety.Windows? = null
    private var absence = GroupProbeSafety.AbsenceProof()
    private var networkName: String? = null // Private memory only; never serialized or logged.
    private var passphrase: CharArray? = null
    private var groupSignature: String? = null
    private var preflightClear = false
    private var preflightCompletedAt: Long? = null
    private var originalWifiBaselineKnown = false
    private var lastWifiSampleStartedAt: Long? = null
    private var createIssued = false
    private var createPending = false
    private var createAccepted: Boolean? = null
    private var createRejectedBusy = false
    private var ownedGroupObserved = false
    private var ownershipProof = GroupProbeSafety.OwnershipProof()
    private val ownershipTainted: Boolean get() = ownershipProof.tainted
    private var evidenceError = false
    private var removeIssued = false
    private var removePending = false
    private var removeAccepted = false
    private var localRetryLocked = false
    private var phaseTimer: Runnable? = null
    private var tick: Runnable? = null
    private var queryTimer: Runnable? = null
    var busy = false
        private set
    val retryLocked: Boolean get() = localRetryLocked || DirectPairingSession.radioGate.locked()
    private val stopping: Boolean get() = windows?.cleanupStartedAt != null

    private val receiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            if (!busy) return
            when (intent.action) {
                WifiP2pManager.WIFI_P2P_CONNECTION_CHANGED_ACTION -> {
                    event("connection_changed_broadcast")
                    if (createIssued && !expirePhase()) queryState(false)
                }
                WifiP2pManager.WIFI_P2P_STATE_CHANGED_ACTION -> {
                    if (intent.getIntExtra(WifiP2pManager.EXTRA_WIFI_STATE, -1) == WifiP2pManager.WIFI_P2P_STATE_DISABLED)
                        cancel("radio_disabled")
                }
            }
        }
    }

    fun report(): JSONObject? {
        observer?.let { record?.put("wifiObservation", it.snapshot()) }
        return record
    }

    fun start() {
        check(Looper.myLooper() == Looper.getMainLooper())
        if (busy || retryLocked || !DirectPairingSession.radioGate.acquire(ownerId)) return
        busy = true; generation++; queryId = 0; queryPending = false; previousQueryCompletedAt = null
        windows = GroupProbeSafety.Windows(now()); absence = GroupProbeSafety.AbsenceProof()
        preflightClear = false; preflightCompletedAt = null; originalWifiBaselineKnown = false; lastWifiSampleStartedAt = null
        createIssued = false; createPending = false; createAccepted = null
        createRejectedBusy = false; ownedGroupObserved = false; ownershipProof = GroupProbeSafety.OwnershipProof(); evidenceError = false
        removeIssued = false; removePending = false; removeAccepted = false; groupSignature = null
        events = JSONArray(); samples = JSONArray()
        record = JSONObject().put("id", UUID.randomUUID().toString()).put("state", "preflight")
            .put("startedAtUtc", Instant.now().toString()).put("userConfirmed", true)
            .put("probeKind", "autonomous_empty_local_go_2_4ghz")
            .put("requestedBand", "2.4GHz").put("setupTimeoutMs", GroupProbeSafety.SETUP_MS)
            .put("observationWindowMs", GroupProbeSafety.OBSERVATION_MS).put("cleanupTimeoutMs", GroupProbeSafety.CLEANUP_MS)
            .put("createGroupIssued", false).put("createGroupAccepted", JSONObject.NULL)
            .put("actual24GhzOwnedEmptyGroupObserved", false).put("peerInvitationIssued", false)
            .put("fileTransferStarted", false).put("networkCredentialsStored", false)
            .put("cancelConnectIssued", false).put("internetTrafficTestExecuted", false)
            .put("rawNetworkNameStored", false).put("rawMacAddressStored", false).put("rawGroupSignatureStored", false)
            .put("continuousInternetPreservationVerified", false).put("events", events).put("groupSamples", samples)
            .put("notes", "Local empty Group Owner capability probe only. No peer connection or application authentication. AP samples and loss events do not prove continuous Internet connectivity. Uncertain creation or cleanup locks later radio experiments in this process.")
        armPhaseTimer()
        try {
            observer = WifiObservation(app, handler).also { it.start() }
            val baseline = observer!!.snapshot()
            originalWifiBaselineKnown = baseline.optInt("baselineNetworkCount", -1) == 1 &&
                baseline.optString("baselineApIdentityState") == "observed" && baseline.optBoolean("networkCallbackRegistered")
            record?.put("originalWifiBaselineVerified", originalWifiBaselineKnown)
            if (!originalWifiBaselineKnown) { finish("not_started_original_wifi_baseline_unknown", false); return }
            if (Build.VERSION.SDK_INT < 29) { finish("not_started_requires_api_29", false); return }
            if (manager == null) { finish("not_started_service_unavailable", false); return }
            val thisGeneration = generation
            channel = manager.initialize(app, Looper.getMainLooper()) {
                if (busy && generation == thisGeneration) {
                    evidenceError = true; ownershipProof.taint()
                    event("channel_disconnected")
                    finish("channel_disconnected_cleanup_unknown", createIssued)
                }
            }
            if (channel == null) { finish("not_started_channel_unavailable", false); return }
            val filter = IntentFilter().apply {
                addAction(WifiP2pManager.WIFI_P2P_CONNECTION_CHANGED_ACTION)
                addAction(WifiP2pManager.WIFI_P2P_STATE_CHANGED_ACTION)
            }
            if (Build.VERSION.SDK_INT >= 33) app.registerReceiver(receiver, filter, Context.RECEIVER_EXPORTED)
            else @Suppress("DEPRECATION") app.registerReceiver(receiver, filter)
            registered = true
            startTicks()
            queryState(true)
            notifyChange("正在检查已有直连；仅探测手机建立空的 2.4 GHz 直连组，不邀请设备。")
        } catch (error: Exception) {
            fault("start_exception", error)
            if (createIssued) cancel("start_exception") else finish("not_started_exception", false)
        }
    }

    private fun createGroup() {
        if (!busy || stopping || expirePhase()) return
        if (Build.VERSION.SDK_INT < 29) { finish("not_started_requires_api_29", false); return }
        if (!sampleWifi("group_probe_before_create")) return
        val preflightFresh = preflightCompletedAt?.let { now() - it in 0..GroupProbeSafety.QUERY_FRESH_MS } == true
        val originalAssociationReady = currentOriginalAssociationReady()
        record?.put("preCreateOriginalAssociationObserved", originalAssociationReady)
        if (!GroupProbeSafety.mayCreate(preflightClear, preflightFresh, retryLocked, queryPending,
                windows!!.expired(now()), originalAssociationReady, originalWifiChangeObserved())) {
            finish("not_started_preflight_refused", false); return
        }
        val random = SecureRandom()
        val alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789"
        fun randomText(length: Int) = CharArray(length) { alphabet[random.nextInt(alphabet.length)] }
        networkName = GroupProbeSafety.temporaryNetworkName(String(randomText(GroupProbeSafety.RANDOM_PREFIX_LENGTH)),
            String(randomText(GroupProbeSafety.RANDOM_SUFFIX_LENGTH)))
        passphrase = randomText(GroupProbeSafety.PASSPHRASE_LENGTH)
        // The temporary config is passed only to the platform. No toString(), credential fields or raw identities enter reports.
        val config: WifiP2pConfig
        try {
            config = WifiP2pConfig.Builder().setNetworkName(networkName!!).setPassphrase(String(passphrase!!))
                .enablePersistentMode(false).setGroupOperatingBand(WifiP2pConfig.GROUP_OWNER_BAND_2GHZ).build()
        } catch (error: Exception) {
            fault("configuration_exception", error); finish("not_started_configuration_exception", false); return
        }
        if (expirePhase()) return
        if (!sampleWifi("group_probe_immediately_before_create")) return
        val associationStillReady = currentOriginalAssociationReady()
        record?.put("preCreateOriginalAssociationObserved", associationStillReady)
        if (preflightCompletedAt?.let { now() - it in 0..GroupProbeSafety.QUERY_FRESH_MS } != true || !associationStillReady) {
            finish("not_started_preflight_or_original_wifi_changed", false); return
        }
        if (expirePhase()) return
        val active = channel ?: run { finish("not_started_channel_unavailable", false); return }
        val thisGeneration = generation
        createIssued = true; createPending = true
        record?.put("state", "creating_group")?.put("createGroupIssued", true)
        event("create_group_invoked")
        try {
            manager!!.createGroup(active, config, object : WifiP2pManager.ActionListener {
                override fun onSuccess() {
                    if (!busy || generation != thisGeneration || !createPending) return
                    createPending = false; createAccepted = true
                    record?.put("createGroupAccepted", true)
                    event(if (stopping) "late_create_group_accepted_during_cleanup" else "create_group_accepted")
                    if (expirePhase()) return
                    queryState(false)
                }
                override fun onFailure(reason: Int) {
                    if (!busy || generation != thisGeneration || !createPending) return
                    createPending = false; createAccepted = false; createRejectedBusy = reason == WifiP2pManager.BUSY
                    record?.put("createGroupAccepted", false)?.put("createGroupFailureCode", reason)
                    event("create_group_rejected").put("reason", reason)
                    if (expirePhase()) return
                    if (!stopping) cancel("create_group_rejected") else queryState(false)
                }
            })
        } catch (error: SecurityException) {
            fault("create_group_permission_denied", error)
            cancel("create_group_permission_denied")
        } catch (error: Exception) {
            // No action callback means the platform operation is not known to have settled.
            fault("create_group_exception", error)
            cancel("create_group_exception")
        }
    }

    private fun queryState(preflight: Boolean) {
        if (!busy || queryPending || expirePhase()) return
        val active = channel ?: return
        val thisGeneration = generation
        val id = ++queryId
        val requestedAt = now()
        queryPending = true
        var groupReceived = false
        var infoReceived = false
        var groupAt: Long? = null
        var infoAt: Long? = null
        var group: WifiP2pGroup? = null
        var info: WifiP2pInfo? = null
        var consumed = false
        var timedOut = false
        val deadline = Runnable {
            if (busy && generation == thisGeneration && queryId == id && !consumed) {
                timedOut = true
                event("paired_native_query_timeout").put("preflight", preflight)
                if (preflight && !createIssued) finish("not_started_preflight_unknown", false)
                else {
                    evidenceError = true; ownershipProof.taint(); absence.reset()
                    if (!stopping) cancel("native_query_timeout")
                    // Keep queryPending: a missing callback does not authorize overlapping query rounds.
                }
            }
        }
        queryTimer = deadline
        handler.postDelayed(deadline, GroupProbeSafety.QUERY_FRESH_MS)
        val receive = {
            if (busy && generation == thisGeneration && queryId == id && !consumed && groupReceived && infoReceived) {
                consumed = true; queryPending = false; handler.removeCallbacks(deadline); queryTimer = null
                val completedAt = now()
                val fresh = !timedOut && GroupProbeSafety.freshPair(requestedAt, groupAt, infoAt, completedAt, previousQueryCompletedAt)
                previousQueryCompletedAt = completedAt
                if (!fresh) {
                    evidenceError = true; ownershipProof.taint(); absence.reset()
                    if (!createIssued) finish("not_started_preflight_stale", false) else if (!stopping) cancel("native_query_stale")
                } else if (!expirePhase()) {
                    if (preflight && !createIssued && !stopping) {
                        preflightClear = GroupProbeSafety.preflightAbsent(group != null, info?.groupFormed, info?.isGroupOwner)
                        preflightCompletedAt = completedAt
                        record?.put("preflightFreshPairedCallbacks", true)?.put("preflightNoExistingGroupVerified", preflightClear)
                        if (preflightClear) createGroup() else finish("not_started_existing_group_or_unknown_refused", false)
                    } else processObservation(group, info, id, requestedAt, groupAt, infoAt, completedAt)
                }
            }
        }
        try {
            manager!!.requestGroupInfo(active) { value ->
                if (busy && generation == thisGeneration && queryId == id && !consumed) {
                    groupReceived = true; group = value; groupAt = now(); receive()
                }
            }
            manager.requestConnectionInfo(active) { value ->
                if (busy && generation == thisGeneration && queryId == id && !consumed) {
                    infoReceived = true; info = value; infoAt = now(); receive()
                }
            }
        } catch (error: SecurityException) {
            timedOut = true; fault("native_query_permission_denied", error)
            if (!createIssued) finish("not_started_preflight_permission_denied", false)
            else cancel("native_query_permission_denied")
        } catch (error: Exception) {
            // One request may already be outstanding. Keep the query occupied until finish.
            timedOut = true; fault("native_query_exception", error)
            if (!createIssued) finish("not_started_preflight_exception", false) else cancel("native_query_exception")
        }
    }

    private fun processObservation(group: WifiP2pGroup?, info: WifiP2pInfo?, id: Int,
                                   requestedAt: Long, groupAt: Long?, infoAt: Long?, completedAt: Long) {
        val signature = group?.let(::signature)
        val snapshot = GroupProbeSafety.Snapshot(group != null, info?.groupFormed, info?.isGroupOwner,
            group?.isGroupOwner, group?.let { if (Build.VERSION.SDK_INT >= 29) it.frequency else null },
            group?.clientList?.size, networkName != null && group?.networkName == networkName,
            signature != null, groupSignature != null && signature == groupSignature, groupSignature != null)
        val observation = GroupProbeSafety.classify(snapshot)
        ownershipProof.observe(id, requestedAt, completedAt, observation, createAccepted == true, snapshot.sameSignature)
        val sample = JSONObject().put("queryId", id).put("atUtc", Instant.now().toString())
            .put("elapsedMs", elapsed(completedAt)).put("groupPresent", group != null)
            .put("groupFormed", info?.groupFormed ?: JSONObject.NULL)
            .put("connectionIsGroupOwner", info?.isGroupOwner ?: JSONObject.NULL)
            .put("groupIsGroupOwner", group?.isGroupOwner ?: JSONObject.NULL)
            .put("frequencyMhz", snapshot.frequencyMhz ?: JSONObject.NULL)
            .put("clientCount", snapshot.clientCount ?: JSONObject.NULL)
            .put("generatedNetworkNameMatched", snapshot.generatedNameMatches)
            .put("signaturePreviouslyObserved", snapshot.signaturePreviouslyObserved)
            .put("sameOwnedSignature", snapshot.sameSignature)
            .put("independentMatchingObservation", ownershipProof.removalSupportedBy(id))
            .put("classification", observation.name.lowercase(Locale.ROOT))
        if (samples.length() < 64) samples.put(sample)
        if (observation == GroupProbeSafety.Observation.TAINTED || observation == GroupProbeSafety.Observation.UNSETTLED) {
            absence.reset(); record?.put("ownershipTainted", true)
            event(if (observation == GroupProbeSafety.Observation.UNSETTLED) "unknown_or_inconsistent_native_observation" else "unattributed_shared_or_wrong_band_group_observed")
            if (!stopping) cancel(if (observation == GroupProbeSafety.Observation.UNSETTLED)
                "unknown_or_inconsistent_native_observation" else "unattributed_shared_or_wrong_band_group")
            return
        }
        if (observation == GroupProbeSafety.Observation.OWN_EMPTY_24_GHZ && createAccepted == true && preflightClear && !ownershipTainted && !evidenceError) {
            if (groupSignature == null) groupSignature = signature
            ownedGroupObserved = true
            record?.put("actual24GhzOwnedEmptyGroupObserved", true)?.put("observedFrequencyMhz", snapshot.frequencyMhz)
            if (!stopping && windows!!.beginObservation(completedAt)) {
                record?.put("state", "observing_owned_empty_group")
                event("owned_empty_24ghz_group_observed")
                armPhaseTimer()
                notifyChange("已回读到手机建立的空 2.4 GHz 直连组；观察原 Wi-Fi 采样 10 秒后自动清理。")
            }
        }
        if (!stopping) {
            if (ownedGroupObserved && observation != GroupProbeSafety.Observation.OWN_EMPTY_24_GHZ) cancel("owned_group_no_longer_observed")
            return
        }
        val cleanupAt = windows!!.cleanupStartedAt!!
        val absentFresh = absence.observe(id, requestedAt, groupAt, infoAt, completedAt, cleanupAt, observation)
        val cleanup = record!!.getJSONObject("cleanup")
        cleanup.put("consecutiveAbsenceSamples", absence.samples).put("absenceObservedSpanMs", absence.observedSpanMs)
            .put("freshPairedAbsence", absentFresh)
        val pending = createPending || removePending || queryPending
        if (GroupProbeSafety.mayReleaseAfterMutation(preflightClear, createAccepted, ownedGroupObserved,
                ownershipTainted, evidenceError, createRejectedBusy, pending, removeAccepted, absence)) {
            cleanup.put("nativeAbsenceVerified", true).put("cancelConnectIssued", false)
            finish(if (createAccepted == false) "create_rejected_no_group_verified" else "owned_group_removed_absence_verified", false)
            return
        }
        // Removal needs a complete fresh pair requested in cleanup, not an earlier observation or a broadcast.
        val removable = requestedAt >= cleanupAt && GroupProbeSafety.mayRemove(preflightClear, createAccepted == true,
            ownershipTainted, evidenceError, observation, createPending || removePending,
            windows!!.expired(now()), ownershipProof.removalSupportedBy(id))
        cleanup.put("groupRemovalOwnershipGatePassed", removable)
        if (removable && !removeIssued) removeOwnedGroup()
    }

    fun cancel(reason: String = "user_cancelled") {
        check(Looper.myLooper() == Looper.getMainLooper())
        if (!busy || stopping) return
        val safeReason = reason.takeIf { it in setOf("user_cancelled", "radio_disabled", "start_exception", "create_group_rejected",
            "create_group_exception", "create_group_permission_denied", "native_query_timeout", "native_query_stale", "native_query_exception", "native_query_permission_denied",
            "unattributed_shared_or_wrong_band_group", "owned_group_no_longer_observed", "setup_timeout",
            "observation_window_completed", "lifecycle_cancelled", "original_wifi_change_observed",
            "unknown_or_inconsistent_native_observation", "wifi_observation_exception") } ?: "caller_cancelled"
        record?.put("finishReason", safeReason)
        if (!createIssued) { finish("not_started_no_radio_mutation", false); return }
        windows!!.beginCleanup(now()); absence.reset()
        record?.put("state", "cleaning_up")?.put("cleanup", JSONObject().put("state", "checking")
            .put("originalOrUnattributedGroupRemoved", false))
        event("cleanup_started").put("reason", safeReason)
        armPhaseTimer()
        queryState(false)
        notifyChange("正在核实并清理本次空直连组，最多 10 秒；原有或不明组不会删除。")
    }

    private fun removeOwnedGroup() {
        if (!busy || !stopping || removeIssued || expirePhase()) return
        val active = channel ?: return
        val thisGeneration = generation
        removeIssued = true; removePending = true
        event("remove_owned_empty_group_invoked")
        try {
            manager!!.removeGroup(active, object : WifiP2pManager.ActionListener {
                override fun onSuccess() {
                    if (!busy || generation != thisGeneration || !removePending) return
                    removePending = false; removeAccepted = true
                    record?.getJSONObject("cleanup")?.put("removeGroupResult", "accepted")
                    event("remove_owned_empty_group_accepted")
                    if (!expirePhase()) queryState(false)
                }
                override fun onFailure(reason: Int) {
                    if (!busy || generation != thisGeneration || !removePending) return
                    removePending = false
                    record?.getJSONObject("cleanup")?.put("removeGroupResult", "rejected")?.put("removeFailureCode", reason)
                    event("remove_owned_empty_group_rejected").put("reason", reason)
                    // No retry after an ambiguous/remove failure, even if a later read is empty.
                    evidenceError = true
                    if (!expirePhase()) queryState(false)
                }
            })
        } catch (error: Exception) { fault("remove_owned_empty_group_exception", error) }
    }

    private fun signature(group: WifiP2pGroup): String? {
        if (group.`interface`.isNullOrBlank()) return null
        return listOf(group.`interface`, group.owner?.deviceAddress.orEmpty().lowercase(Locale.ROOT),
            if (Build.VERSION.SDK_INT >= 30) group.networkId.toString() else "api29").joinToString("|")
    }

    private fun startTicks() {
        val thisGeneration = generation
        tick = object : Runnable {
            override fun run() {
                if (!busy || generation != thisGeneration) return
                if (!expirePhase()) {
                    if (sampleWifi(if (stopping) "group_probe_cleanup" else "group_probe")) {
                        if (!stopping && originalWifiChangeObserved()) cancel("original_wifi_change_observed")
                        if (createIssued) queryState(false)
                    }
                }
                if (busy) handler.postDelayed(this, 1_000)
            }
        }.also { handler.postDelayed(it, 1_000) }
    }

    private fun expirePhase(): Boolean {
        if (!busy) return true
        val activeWindows = windows ?: return true
        if (!activeWindows.expired(now())) return false
        when {
            stopping -> finish("cleanup_timeout_unknown", true)
            activeWindows.observationStartedAt != null -> cancel("observation_window_completed")
            else -> cancel("setup_timeout")
        }
        return true
    }

    private fun armPhaseTimer() {
        phaseTimer?.let(handler::removeCallbacks)
        phaseTimer = Runnable { expirePhase() }.also { handler.postDelayed(it, (windows!!.activeDeadline - now()).coerceAtLeast(0)) }
    }

    private fun fault(kind: String, error: Exception) {
        evidenceError = true; ownershipProof.taint(); absence.reset()
        event(kind).put("errorType", error.javaClass.simpleName)
        record?.put("ownershipTainted", true)
    }

    private fun finish(cleanupState: String, uncertain: Boolean) {
        if (!busy) return
        busy = false; generation++
        phaseTimer?.let(handler::removeCallbacks); tick?.let(handler::removeCallbacks); queryTimer?.let(handler::removeCallbacks)
        phaseTimer = null; tick = null; queryTimer = null
        localRetryLocked = localRetryLocked || uncertain
        val cleanup = record?.optJSONObject("cleanup") ?: JSONObject().also { record?.put("cleanup", it) }
        cleanup.put("state", cleanupState).put("originalOrUnattributedGroupRemoved", false)
            .put("createCallbackPendingAtFinish", createPending).put("cancelConnectIssued", false)
            .put("removeCallbackPendingAtFinish", removePending).put("nativeQueryPendingAtFinish", queryPending)
            .put("ownedGroupObserved", ownedGroupObserved).put("createRejectedBusy", createRejectedBusy)
            .put("groupRemovalAcknowledged", removeAccepted)
            .put("ownershipTainted", ownershipTainted).put("nativeEvidenceError", evidenceError)
        try { observer?.let { record?.put("wifiObservation", it.stop()) } }
        catch (error: Exception) { localRetryLocked = true; cleanup.put("observationStopErrorType", error.javaClass.simpleName) }
        if (registered) {
            try { app.unregisterReceiver(receiver); cleanup.put("receiverUnregistered", true) }
            catch (error: Exception) { localRetryLocked = true; cleanup.put("receiverUnregisterErrorType", error.javaClass.simpleName) }
            registered = false
        }
        try {
            if (Build.VERSION.SDK_INT >= 27) channel?.close()
            else if (channel != null) localRetryLocked = true
            cleanup.put("channelClosed", channel == null || Build.VERSION.SDK_INT >= 27)
        }
        catch (error: Exception) { localRetryLocked = true; cleanup.put("channelCloseErrorType", error.javaClass.simpleName) }
        channel = null; networkName = null; passphrase?.fill('\u0000'); passphrase = null; groupSignature = null
        DirectPairingSession.radioGate.release(ownerId, localRetryLocked)
        record?.put("state", "finished")?.put("finishedAtUtc", Instant.now().toString())
            ?.put("elapsedMs", elapsed(now()))?.put("retryLocked", retryLocked)
        notifyChange(if (retryLocked) "2.4 GHz 空组探针已结束，清理有不确定性；后续无线实验已锁定，请查看报告。"
            else if (ownedGroupObserved) "2.4 GHz 空组探针已结束并核实清理；仅验证本机建组，尚未验证设备互连。"
            else "2.4 GHz 空组探针未建立可确认的自有组，结果已记录。")
    }

    private fun event(kind: String): JSONObject = JSONObject().put("kind", kind).put("atUtc", Instant.now().toString())
        .put("elapsedMs", elapsed(now())).also { if (events.length() < 160) events.put(it) }
    private fun elapsed(value: Long): Long = value - (windows?.startedAt ?: value)
    private fun sampleWifi(stage: String): Boolean = try {
        val sampledAt = now()
        observer?.sample(stage)
        lastWifiSampleStartedAt = sampledAt
        true
    } catch (error: Exception) {
        fault("wifi_observation_exception", error)
        if (createIssued) cancel("wifi_observation_exception") else finish("not_started_wifi_observation_exception", false)
        false
    }
    private fun currentOriginalAssociationReady(): Boolean {
        val snapshot = observer?.snapshot() ?: return false
        val wifiSamples = snapshot.optJSONArray("samples") ?: return false
        val lastSample = wifiSamples.optJSONObject(wifiSamples.length() - 1) ?: return false
        val primary = lastSample.optJSONObject("primaryWifiManagerObservation") ?: return false
        val baselineToken = snapshot.optString("baselineApToken").takeIf { !snapshot.isNull("baselineApToken") && it.isNotBlank() }
        val currentToken = primary.optString("bssidToken").takeIf { !primary.isNull("bssidToken") && it.isNotBlank() }
        val networks = lastSample.optJSONArray("wifiNetworks")
        val originalNetworkVisible = networks != null && (0 until networks.length()).any {
            networks.optJSONObject(it)?.optBoolean("wasBaselineWifiNetwork") == true
        }
        return GroupProbeSafety.originalAssociationReady(originalWifiBaselineKnown,
            primary.optString("state") == "observed", baselineToken != null && currentToken == baselineToken,
            originalNetworkVisible, lastWifiSampleStartedAt?.let { now() - it in 0..GroupProbeSafety.QUERY_FRESH_MS } == true,
            originalWifiChangeObserved())
    }
    private fun originalWifiChangeObserved(): Boolean = observer?.snapshot()?.let {
        it.optBoolean("baselineWifiNetworkLossEventObserved") || it.optBoolean("baselineNetworkApChangeEventObserved") ||
            it.optInt("changedApSamples", 0) > 0
    } ?: false
    private fun now(): Long = SystemClock.elapsedRealtime()
    private fun notifyChange(message: String) {
        try { changed(message) } catch (error: Exception) { record?.put("uiCallbackErrorType", error.javaClass.simpleName) }
    }
}
