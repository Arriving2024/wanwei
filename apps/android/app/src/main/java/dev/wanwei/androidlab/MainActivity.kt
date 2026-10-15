package dev.wanwei.androidlab

import android.Manifest
import android.app.Activity
import android.app.AlertDialog
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.location.LocationManager
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.net.wifi.WifiManager
import android.net.wifi.aware.WifiAwareManager
import android.net.wifi.p2p.WifiP2pDevice
import android.net.wifi.p2p.WifiP2pManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.provider.Settings
import android.view.WindowInsets
import android.widget.Button
import android.widget.CheckBox
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.FileOutputStream
import java.security.SecureRandom
import java.security.MessageDigest
import java.time.Instant
import java.util.Locale
import java.util.UUID
import java.util.concurrent.Executors
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/** Native Direct lab with opt-in, pinned mutual-TLS P0 synthetic file transfer. */
class MainActivity : Activity() {
    private val handler = Handler(Looper.getMainLooper())
    private val writer = reportWriter
    private val runId = UUID.randomUUID().toString()
    private val tokenKey = ByteArray(32).also { SecureRandom().nextBytes(it) }
    private val startedAt = Instant.now().toString()
    private val scanOwnerId = "scan:$runId"
    private val gateRefresh = object : Runnable {
        override fun run() {
            if (!started || destroyed) return
            updateButtons()
            handler.postDelayed(this, 1_000)
        }
    }
    private val bg = Color.rgb(16, 21, 30)
    private val panel = Color.rgb(26, 34, 47)
    private val muted = Color.rgb(166, 181, 201)
    private val accent = Color.rgb(115, 233, 196)
    private lateinit var content: LinearLayout
    private lateinit var capabilityText: TextView
    private lateinit var stateText: TextView
    private lateinit var peersText: TextView
    private lateinit var reportText: TextView
    private lateinit var scanButton: Button
    private lateinit var stopButton: Button
    private lateinit var probeButton: Button
    private lateinit var exportButton: Button
    private lateinit var selectPeerButton: Button
    private lateinit var connectButton: Button
    private lateinit var preferPhoneGoCheckBox: CheckBox
    private lateinit var cancelPairButton: Button
    private lateinit var pairingText: TextView
    private lateinit var pairing: DirectPairingSession
    private lateinit var fileExperimentCheckBox: CheckBox
    private lateinit var localPinText: TextView
    private lateinit var transferStateText: TextView
    private lateinit var peerPinInput: EditText
    private lateinit var remoteAddressInput: EditText
    private lateinit var peerPortInput: EditText
    private lateinit var trustPeerButton: Button
    private lateinit var receiveTestButton: Button
    private lateinit var sendTestButton: Button
    private lateinit var cancelTransferButton: Button
    private lateinit var acceptOfferButton: Button
    private lateinit var rejectOfferButton: Button
    private lateinit var identity: P0Identity
    private lateinit var transfer: P0TransferSession
    private var confirmedPeerPin: String? = null
    private var offerPending = false
    private lateinit var usbLabStateText: TextView
    private var usbLabNonce: String? = null
    private var usbLabPeerPin: String? = null
    private var usbLabExpiresAtMs = 0L
    private val consumedLabCommandIds = hashSetOf<String>()
    private val consumedLabNonces = hashSetOf<String>()
    private val labPump = object : Runnable {
        override fun run() {
            if (!started || destroyed) return
            activateUsbLabIfRequested()
            if (usbLabNonce != null) {
                if (SystemClock.elapsedRealtime() >= usbLabExpiresAtMs) {
                    deactivateUsbLab("expired")
                    if (pairing.busy) pairing.cancel("usb_lab_window_expired")
                } else consumeUsbLabCommand()
            }
            handler.postDelayed(this, 250)
        }
    }
    private val livePeers = linkedMapOf<String, Pair<WifiP2pDevice, Long>>()
    private var selectedPeerToken: String? = null
    private var pendingPairPermission = false
    private var p2p: WifiP2pManager? = null
    private var channel: WifiP2pManager.Channel? = null
    private var p2pState: Int? = null
    private var channelState = "not_initialized"
    private var channelGeneration = 0
    private var registered = false
    private var started = false
    private var destroyed = false
    private var pendingPermissionScan = false
    private var probe = JSONObject()
    private var currentScan: JSONObject? = null
    private val peers = linkedMapOf<String, JSONObject>()
    private var scanning = false
    private var stopping = false
    private var generation = 0
    private var scanStarted = 0L
    private var scanTimeout: Runnable? = null
    private var startTimeout: Runnable? = null
    private var stopTimeout: Runnable? = null
    private var stopFinish: (() -> Unit)? = null
    private var exportSnapshot: String? = null

    private val receiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            if (destroyed) return
            when (intent.action) {
                WifiP2pManager.WIFI_P2P_STATE_CHANGED_ACTION -> {
                    p2pState = intent.getIntExtra(WifiP2pManager.EXTRA_WIFI_STATE, -1)
                    updateP2pProbe()
                    if (scanning && p2pState == WifiP2pManager.WIFI_P2P_STATE_DISABLED) {
                        finishScan("radio_disabled", "系统报告 Wi-Fi Direct 已关闭。")
                    }
                }
                WifiP2pManager.WIFI_P2P_PEERS_CHANGED_ACTION -> if (scanning) requestPeers(generation)
                WifiAwareManager.ACTION_WIFI_AWARE_STATE_CHANGED -> if (!scanning && !stopping) probeCapabilities()
            }
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        exportSnapshot = savedInstanceState?.getString(PENDING_EXPORT_STATE)
        pairing = DirectPairingSession(this, ::anonymousToken) { message ->
            if (!destroyed) {
                if (message.isNotBlank()) pairingText.text = message
                updateButtons()
            }
            persistReport("pairing", allowAfterDestroy = true)
        }
        buildUi()
        pairing.beforeCleanup = { if (::transfer.isInitialized) transfer.cancel("DIRECT_PATH_UNVERIFIED") }
        prepareIdentity()
        initializeP2p()
        probeCapabilities()
    }

    override fun onSaveInstanceState(outState: Bundle) {
        exportSnapshot?.let { outState.putString(PENDING_EXPORT_STATE, it) }
        super.onSaveInstanceState(outState)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        if (started) activateUsbLabIfRequested()
    }

    override fun onStart() {
        super.onStart()
        started = true
        val filter = IntentFilter().apply {
            addAction(WifiP2pManager.WIFI_P2P_STATE_CHANGED_ACTION)
            addAction(WifiP2pManager.WIFI_P2P_PEERS_CHANGED_ACTION)
            addAction(WifiAwareManager.ACTION_WIFI_AWARE_STATE_CHANGED)
        }
        if (!registered) {
            // These are protected system broadcasts, including broadcasts originating in the Wi-Fi service.
            if (Build.VERSION.SDK_INT >= 33) registerReceiver(receiver, filter, Context.RECEIVER_EXPORTED)
            else @Suppress("DEPRECATION") registerReceiver(receiver, filter)
            registered = true
        }
        requestP2pState()
        handler.removeCallbacks(gateRefresh)
        handler.post(gateRefresh)
        handler.removeCallbacks(labPump)
        handler.post(labPump)
    }

    override fun onResume() {
        super.onResume()
        if (!scanning && !stopping) probeCapabilities()
    }

    override fun onStop() {
        started = false
        intent?.removeExtra("wanwei_lab_nonce")
        handler.removeCallbacks(gateRefresh)
        handler.removeCallbacks(labPump)
        deactivateUsbLab("activity_stopped")
        pendingPermissionScan = false
        if (scanning) finishScan("activity_stopped", "界面离开前台，已结束本次扫描。")
        pendingPairPermission = false
        if (::transfer.isInitialized) transfer.cancel("CANCELLED")
        if (pairing.awaitingSystemNegotiation) pairing.noteLifecycle("activity_stopped_during_bounded_system_negotiation")
        else if (pairing.busy) pairing.cancel("activity_stopped")
        if (registered) {
            unregisterReceiver(receiver)
            registered = false
        }
        super.onStop()
    }

    override fun onDestroy() {
        deactivateUsbLab("activity_destroyed")
        if (::transfer.isInitialized) transfer.cancel("CANCELLED")
        pairing.cancel("activity_destroyed")
        if (scanning) finishScan("activity_destroyed", "界面关闭，已结束本次扫描。")
        // A closing Activity cannot wait indefinitely for the platform's stop callback.
        if (stopping) {
            currentScan?.put("stopResult", "channel_closed_before_stop_confirmation")
            stopFinish?.invoke()
        }
        destroyed = true
        handler.removeCallbacksAndMessages(null)
        closeChannel(channel)
        channel = null
        super.onDestroy()
    }

    private fun initializeP2p() {
        try {
            p2p = getSystemService(Context.WIFI_P2P_SERVICE) as? WifiP2pManager
            val manager = p2p
            if (manager == null) {
                channelState = "service_unavailable"
                return
            }
            val thisChannelGeneration = ++channelGeneration
            channel = manager.initialize(this, mainLooper) {
                if (thisChannelGeneration != channelGeneration || destroyed) return@initialize
                channelState = "channel_disconnected"
                channel = null
                if (scanning) finishScan("channel_disconnected", "Wi-Fi Direct 通道已断开，可重新检查后重试。")
                updateP2pProbe()
            }
            channelState = if (channel == null) "initialize_returned_null" else "initialized"
        } catch (error: Exception) {
            channelState = "initialize_${error.javaClass.simpleName}"
        }
    }

    private fun probeCapabilities() {
        if (destroyed) return
        if (channel == null && !scanning && !stopping) initializeP2p()
        val awareFeature = packageManager.hasSystemFeature(PackageManager.FEATURE_WIFI_AWARE)
        val aware = JSONObject().put("frameworkApiPresent", true) // minSdk is 26.
            .put("hardwareFeatureReported", awareFeature)
        try {
            val manager = getSystemService(Context.WIFI_AWARE_SERVICE) as? WifiAwareManager
            aware.put("servicePresent", manager != null)
            if (manager != null) {
                aware.put("runtimeAvailable", manager.isAvailable)
                val characteristics = manager.characteristics
                aware.put("characteristicsAvailable", characteristics != null)
                if (Build.VERSION.SDK_INT >= 34 && characteristics != null) {
                    aware.put("pairingSupported", characteristics.isAwarePairingSupported)
                    aware.put("pairingCipherSuites", characteristics.supportedPairingCipherSuites)
                } else {
                    aware.put("pairingSupported", JSONObject.NULL)
                    aware.put("pairingProbeState", if (Build.VERSION.SDK_INT < 34) "requires_api_34" else "characteristics_unavailable")
                }
            } else aware.put("runtimeAvailable", JSONObject.NULL).put("pairingSupported", JSONObject.NULL)
        } catch (error: Exception) {
            aware.put("probeError", error.javaClass.simpleName)
            aware.put("runtimeAvailable", JSONObject.NULL).put("pairingSupported", JSONObject.NULL)
        }
        probe = JSONObject().put("observedAtUtc", Instant.now().toString())
            .put("device", JSONObject().put("manufacturer", Build.MANUFACTURER)
                .put("model", Build.MODEL).put("androidVersion", Build.VERSION.RELEASE)
                .put("apiLevel", Build.VERSION.SDK_INT).put("securityPatch", Build.VERSION.SECURITY_PATCH))
            .put("wifiAware", aware)
            .put("wifiDirect", JSONObject().put("frameworkApiPresent", true)
                .put("hardwareFeatureReported", packageManager.hasSystemFeature(PackageManager.FEATURE_WIFI_DIRECT))
                .put("servicePresent", p2p != null).put("channelState", channelState)
                .put("runtimeP2pState", p2pStateName()))
            .put("wifiConcurrency", concurrencySnapshot())
            .put("environment", environmentSnapshot())
        renderCapabilities()
        persistReport("probe")
        requestP2pState()
    }

    private fun concurrencySnapshot(): JSONObject {
        val value = JSONObject().put("directConcurrencyVerified", false)
            .put("notes", "STA+STA capability queries are clues only. They do not establish STA+P2P or Aware interoperability.")
        val wifi = applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager
        fun query(name: String, minimumApi: Int, call: () -> Boolean) {
            if (wifi == null || Build.VERSION.SDK_INT < minimumApi) {
                value.put(name, JSONObject.NULL).put("${name}State", if (wifi == null) "service_unavailable" else "requires_api_$minimumApi")
            } else try { value.put(name, call()).put("${name}State", "queried") }
            catch (error: Exception) { value.put(name, JSONObject.NULL).put("${name}State", "error_${error.javaClass.simpleName}") }
        }
        query("staLocalOnlySupported", 31) { if (Build.VERSION.SDK_INT >= 31) wifi!!.isStaConcurrencyForLocalOnlyConnectionsSupported else false }
        query("staMultiInternetSupported", 33) { if (Build.VERSION.SDK_INT >= 33) wifi!!.isStaConcurrencyForMultiInternetSupported else false }
        query("makeBeforeBreakSupported", 31) { if (Build.VERSION.SDK_INT >= 31) wifi!!.isMakeBeforeBreakWifiSwitchingSupported else false }
        return value
    }

    private fun requestP2pState() {
        val manager = p2p ?: return
        val activeChannel = channel ?: return
        if (Build.VERSION.SDK_INT >= 29) {
            try {
                manager.requestP2pState(activeChannel) { state ->
                    if (!destroyed) {
                        p2pState = state
                        updateP2pProbe()
                    }
                }
            } catch (_: Exception) {
                // Unknown remains unknown; never infer runtime availability from hardware support.
            }
        }
    }

    private fun updateP2pProbe() {
        probe.optJSONObject("wifiDirect")?.put("runtimeP2pState", p2pStateName())?.put("channelState", channelState)
        renderCapabilities()
        persistReport("probe")
    }

    private fun p2pStateName(): String = when (p2pState) {
        WifiP2pManager.WIFI_P2P_STATE_ENABLED -> "enabled"
        WifiP2pManager.WIFI_P2P_STATE_DISABLED -> "disabled"
        else -> "unknown"
    }

    private fun environmentSnapshot(): JSONObject {
        val value = JSONObject().put("discoveryPermission", if (hasDiscoveryPermission()) "granted" else "not_granted")
            .put("requiredPermission", discoveryPermission()).put("locationModeEnabled", locationEnabled() ?: JSONObject.NULL)
        try {
            val wifi = applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager
            value.put("wifiEnabled", wifi?.isWifiEnabled ?: JSONObject.NULL)
            val connectivity = getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
            val network = connectivity.activeNetwork
            val capabilities = network?.let { connectivity.getNetworkCapabilities(it) }
            value.put("defaultNetworkPresent", network != null)
                .put("defaultNetworkWifi", capabilities?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) ?: JSONObject.NULL)
                .put("defaultNetworkCellular", capabilities?.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) ?: JSONObject.NULL)
                .put("defaultNetworkVpn", capabilities?.hasTransport(NetworkCapabilities.TRANSPORT_VPN) ?: JSONObject.NULL)
                .put("defaultNetworkSystemValidated", capabilities?.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED) ?: JSONObject.NULL)
        } catch (error: Exception) {
            value.put("environmentProbeError", error.javaClass.simpleName)
        }
        return value
    }

    private fun locationEnabled(): Boolean? = try {
        val location = getSystemService(Context.LOCATION_SERVICE) as LocationManager
        if (Build.VERSION.SDK_INT >= 28) location.isLocationEnabled
        else location.isProviderEnabled(LocationManager.GPS_PROVIDER) || location.isProviderEnabled(LocationManager.NETWORK_PROVIDER)
    } catch (_: Exception) { null }

    private fun discoveryPermission() = if (Build.VERSION.SDK_INT >= 33) Manifest.permission.NEARBY_WIFI_DEVICES else Manifest.permission.ACCESS_FINE_LOCATION
    private fun hasDiscoveryPermission() = checkSelfPermission(discoveryPermission()) == PackageManager.PERMISSION_GRANTED
    private fun discoveryPermissionRequest(): Array<String> = when (Build.VERSION.SDK_INT) {
        in 31..32 -> arrayOf(Manifest.permission.ACCESS_COARSE_LOCATION, Manifest.permission.ACCESS_FINE_LOCATION)
        else -> arrayOf(discoveryPermission())
    }

    private fun requestScan() {
        if (scanning || stopping || pairing.busy || pairing.retryLocked || DirectPairingSession.radioGate.occupied()) return
        if (!hasDiscoveryPermission()) {
            pendingPermissionScan = true
            stateText.text = if (Build.VERSION.SDK_INT >= 33) "请允许“附近设备”，用于发现 Wi-Fi Direct 候选。" else "此 Android 版本要求定位权限才能扫描；程序不会读取位置。"
            requestPermissions(discoveryPermissionRequest(), PERMISSION_REQUEST)
            return
        }
        startScan()
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == PAIR_PERMISSION_REQUEST) {
            val proceed = pendingPairPermission
            pendingPairPermission = false
            probeCapabilities()
            if (proceed && started && hasDiscoveryPermission()) confirmPairing()
            else pairingText.text = "所需附近设备权限未授予，本次未发起连接。"
            return
        }
        if (requestCode != PERMISSION_REQUEST) return
        val shouldScan = pendingPermissionScan
        pendingPermissionScan = false
        probeCapabilities()
        if (hasDiscoveryPermission() && shouldScan && started) startScan()
        else if (!hasDiscoveryPermission()) {
            recordBlocked("permission_denied", "扫描权限未授予。可再次点击扫描，或在系统应用设置中允许权限。")
        }
    }

    private fun startScan() {
        if (!started || scanning || stopping || pairing.busy || pairing.retryLocked || DirectPairingSession.radioGate.occupied()) return
        // Permission can be revoked between the initial button action and this callback.
        if (!hasDiscoveryPermission()) {
            recordBlocked("permission_denied", "扫描权限当前未授予，请重新允许后再扫描。")
            return
        }
        probeCapabilities()
        if (!packageManager.hasSystemFeature(PackageManager.FEATURE_WIFI_DIRECT)) {
            recordBlocked("feature_not_reported", "系统未声明 Wi-Fi Direct 支持，无法开始此项扫描。")
            return
        }
        if (locationEnabled() == false) {
            recordBlocked("location_mode_disabled", "系统定位开关已关闭。Android 的附近 Wi-Fi 发现需要此开关；可点下方“系统定位设置”手动开启。")
            return
        }
        val manager = p2p
        val activeChannel = channel
        if (manager == null || activeChannel == null) {
            recordBlocked("channel_unavailable", "Wi-Fi Direct 通道不可用，点击“重新检查能力”后重试。")
            return
        }
        if (p2pState == WifiP2pManager.WIFI_P2P_STATE_DISABLED) {
            recordBlocked("p2p_disabled", "系统报告 Wi-Fi Direct 关闭。请检查系统 Wi-Fi 状态后重试。")
            return
        }
        if (!DirectPairingSession.radioGate.acquire(scanOwnerId)) return
        peers.clear()
        livePeers.clear()
        selectedPeerToken = null
        selectPeerButton.text = "选择附近候选"
        generation += 1
        val thisGeneration = generation
        scanStarted = SystemClock.elapsedRealtime()
        currentScan = newScan("starting").put("environmentBefore", environmentSnapshot())
        scanning = true
        updateButtons()
        peersText.text = "等待系统发现回调……"
        stateText.text = "正在请求 Wi-Fi Direct 扫描……"
        startTimeout = Runnable {
            if (scanning && generation == thisGeneration) finishScan("start_callback_timeout", "系统未在 5 秒内确认扫描请求，已请求停止。")
        }.also { handler.postDelayed(it, 5_000) }
        try {
            manager.discoverPeers(activeChannel, object : WifiP2pManager.ActionListener {
                override fun onSuccess() {
                    if (!scanning || generation != thisGeneration) return
                    startTimeout?.let(handler::removeCallbacks)
                    currentScan?.put("discoveryRequestAccepted", true)?.put("state", "discovering")
                    currentScan?.put("discoveryAcceptedElapsedMs", SystemClock.elapsedRealtime() - scanStarted)
                    stateText.text = "扫描中 · 15 秒后自动停止。发现候选不代表已连接或兼容。"
                    requestPeers(thisGeneration)
                    scanTimeout = Runnable {
                        if (scanning && generation == thisGeneration) finishScan("completed", "扫描时间结束。")
                    }.also { handler.postDelayed(it, 15_000) }
                }
                override fun onFailure(reason: Int) {
                    if (!scanning || generation != thisGeneration) return
                    currentScan?.put("discoveryRequestAccepted", false)?.put("platformFailureCode", reason)
                    finishScan("discovery_rejected", "系统未接受扫描：${failureName(reason)}。", requestStop = false)
                }
            })
        } catch (error: SecurityException) {
            currentScan?.put("discoveryRequestAccepted", false)?.put("errorType", error.javaClass.simpleName)
            finishScan("permission_denied", "系统拒绝了扫描权限，请检查应用权限后重试。", requestStop = false)
        } catch (error: Exception) {
            currentScan?.put("errorType", error.javaClass.simpleName)
            finishScan("discovery_exception", "扫描调用失败：${error.javaClass.simpleName}。", requestStop = false)
        }
    }

    private fun requestPeers(thisGeneration: Int) {
        if (!scanning || generation != thisGeneration || destroyed) return
        if (!hasDiscoveryPermission()) {
            finishScan("permission_revoked", "扫描权限已被撤销，已结束本次扫描。")
            return
        }
        val manager = p2p ?: return
        val activeChannel = channel ?: return
        try {
            manager.requestPeers(activeChannel) { list ->
                if (!scanning || generation != thisGeneration || destroyed) return@requestPeers
                currentScan?.put("peerListCallbackReceived", true)
                currentScan?.put("lastPeerListElapsedMs", SystemClock.elapsedRealtime() - scanStarted)
                currentScan?.put("lastPeerListCount", list.deviceList.size)
                if (list.deviceList.size > 100) currentScan?.put("candidateLimitReached", true)
                currentScan?.put("redactedCandidatesInLatestList", list.deviceList.count {
                    it.deviceAddress.isNullOrBlank() || it.deviceAddress == "02:00:00:00:00:00"
                })
                list.deviceList.take(100).forEach { device ->
                    val address = device.deviceAddress.orEmpty()
                    if (address.isNotBlank() && address != "02:00:00:00:00:00") {
                        val token = anonymousToken(address)
                        if (peers.containsKey(token) || peers.size < 100) {
                            peers[token] = JSONObject().put("sessionToken", token).put("status", peerStatus(device.status))
                            livePeers[token] = Pair(WifiP2pDevice(device), SystemClock.elapsedRealtime())
                        } else currentScan?.put("candidateLimitReached", true)
                    }
                }
                if (peers.isEmpty()) peersText.text = "目前候选为 0。对端需处于可发现状态；零结果不能据此否定兼容性。"
                else peersText.text = peers.values.joinToString("\n") { "附近候选 ${it.getString("sessionToken")}  ·  ${peerStatusLabel(it.getString("status"))}" }
                updateButtons()
            }
        } catch (error: SecurityException) {
            currentScan?.put("peerListErrorType", error.javaClass.simpleName)
            finishScan("permission_revoked", "系统拒绝读取附近候选的权限，已结束本次扫描。")
        } catch (error: Exception) {
            currentScan?.put("peerListErrorType", error.javaClass.simpleName)
            finishScan("peer_list_error", "读取候选失败：${error.javaClass.simpleName}。")
        }
    }

    private fun finishScan(result: String, message: String, requestStop: Boolean = true) {
        if (!scanning) return
        scanning = false
        stopping = requestStop
        startTimeout?.let(handler::removeCallbacks)
        scanTimeout?.let(handler::removeCallbacks)
        currentScan?.put("state", result)?.put("finishedAtUtc", Instant.now().toString())
            ?.put("elapsedMs", SystemClock.elapsedRealtime() - scanStarted)
            ?.put("candidateCount", peers.size)?.put("candidates", JSONArray(peers.values.toList()))
            ?.put("environmentAfter", environmentSnapshot())
        stateText.text = if (requestStop) "$message 正在确认停止……" else message
        updateButtons()
        var saved = false
        val finish = {
            if (!saved) {
                saved = true
                stopping = false
                val stopConfirmed = currentScan?.optString("stopResult") == "confirmed"
                DirectPairingSession.radioGate.release(scanOwnerId, requestStop && !stopConfirmed)
                stopTimeout?.let(handler::removeCallbacks)
                stopFinish = null
                if (!destroyed) {
                    val stopResult = currentScan?.optString("stopResult")
                    stateText.text = message + if (stopResult == "confirmed") " 已确认停止。候选 ${peers.size} 个。"
                    else if (requestStop) " 停止未获系统确认，请查看报告。" else ""
                    updateButtons()
                }
                persistReport("scan")
            }
        }
        stopFinish = finish
        val manager = p2p
        val activeChannel = channel
        if (!requestStop || manager == null || activeChannel == null) {
            currentScan?.put("stopResult", if (!requestStop) "not_needed_request_rejected" else "channel_unavailable")
            finish()
            return
        }
        stopTimeout = Runnable {
            currentScan?.put("stopResult", "callback_timeout")
            // Release only this app's channel. Never remove a group or an OS pairing.
            releaseChannel(activeChannel, "closed_after_stop_timeout")
            finish()
        }.also { handler.postDelayed(it, 2_000) }
        try {
            manager.stopPeerDiscovery(activeChannel, object : WifiP2pManager.ActionListener {
                override fun onSuccess() {
                    if (saved) return
                    currentScan?.put("stopResult", "confirmed")
                    finish()
                }
                override fun onFailure(reason: Int) {
                    if (saved) return
                    currentScan?.put("stopResult", "rejected")?.put("stopFailureCode", reason)
                    releaseChannel(activeChannel, "closed_after_stop_rejection")
                    finish()
                }
            })
        } catch (error: Exception) {
            currentScan?.put("stopResult", "exception")?.put("stopErrorType", error.javaClass.simpleName)
            releaseChannel(activeChannel, "closed_after_stop_exception")
            finish()
        }
    }

    private fun recordBlocked(reason: String, message: String) {
        peers.clear()
        livePeers.clear()
        selectedPeerToken = null
        currentScan = newScan(reason).put("finishedAtUtc", Instant.now().toString())
            .put("elapsedMs", 0).put("discoveryRequestAccepted", false).put("candidateCount", 0)
            .put("candidates", JSONArray()).put("environmentBefore", environmentSnapshot())
            .put("stopResult", "not_started")
        stateText.text = message
        peersText.text = "扫描尚未开始。"
        updateButtons()
        persistReport("scan")
    }

    private fun closeChannel(value: WifiP2pManager.Channel?) {
        // Channel.close() was added in API 27. On API 26 the stopped channel is released with the process.
        if (Build.VERSION.SDK_INT >= 27) runCatching { value?.close() }
    }

    private fun releaseChannel(value: WifiP2pManager.Channel, reason: String) {
        if (channel === value) {
            channelGeneration += 1
            channel = null
            channelState = reason
        }
        closeChannel(value)
    }

    private fun newScan(state: String) = JSONObject().put("id", UUID.randomUUID().toString())
        .put("state", state).put("startedAtUtc", Instant.now().toString())
        .put("requestedDiscoveryWindowMs", 15_000).put("discoveryRequestAccepted", JSONObject.NULL)
        .put("peerListCallbackReceived", false).put("candidateCount", 0)
        .put("candidateLimit", 100).put("candidateLimitReached", false)
        .put("peerListMayIncludeCachedDevices", true)

    private fun anonymousToken(value: String): String {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(tokenKey, "HmacSHA256"))
        return mac.doFinal(value.lowercase(Locale.ROOT).toByteArray(Charsets.UTF_8))
            .take(6).joinToString("") { "%02x".format(it.toInt() and 255) }
    }

    private fun makeReport(kind: String): String = JSONObject()
        .put("schema", "wanwei.android.p0.v1").put("appVersion", "0.3.1")
        .put("kind", kind).put("generatedAtUtc", Instant.now().toString())
        .put("runId", runId).put("runStartedAtUtc", startedAt).put("capabilityProbe", probe)
        .put("scan", currentScan ?: JSONObject.NULL)
        .put("pairing", pairing.report() ?: JSONObject.NULL)
        .put("evidenceBoundary", JSONObject().put("capabilityProbeExecuted", true)
            .put("pairingImplemented", true).put("actualDirectConnectionObserved", pairing.report()?.optBoolean("actualConnectionObserved") ?: false)
            .put("applicationIdentityAuthenticated", false).put("fileTransferImplemented", true)
            .put("applicationTransferEvidence", "files/reports/latest-transfer.json")
            .put("originalWifiPreservationVerified", false).put("crossPlatformCompatibilityVerified", false)
            .put("screenSharingImplemented", false)
            .put("notes", "Feature flags and API availability do not prove interoperability. Discovery may return cached peers. Default-network snapshots do not prove the original AP was retained."))
        .put("privacy", JSONObject().put("candidateToken", "HMAC-SHA256 truncated to 48 bits, random key for this activity run, key not persisted")
            .put("rawMacStored", false).put("ssidStored", false).put("candidateNameStored", false)
            .put("rawDeviceIdStored", false).put("applicationPayloadSent", JSONObject.NULL)
            .put("notes", "Discovery and pairing reports do not report file success. Opt-in pinned mutual-TLS synthetic file traffic has separate latest-transfer.json evidence; no user files or private keys are exported."))
        .toString(2)

    private fun persistReport(kind: String, allowAfterDestroy: Boolean = false) {
        if (destroyed && !allowAfterDestroy) return
        val snapshot = makeReport(kind)
        writer.execute {
            var temporary: File? = null
            try {
                val directory = File(filesDir, "reports").apply { mkdirs() }
                val target = File(directory, "latest-$kind.json")
                val staging = File.createTempFile("latest-$kind-", ".tmp", directory)
                temporary = staging
                FileOutputStream(staging).use { stream ->
                    stream.write(snapshot.toByteArray(Charsets.UTF_8))
                    stream.fd.sync()
                }
                if (!staging.renameTo(target)) {
                    throw java.io.IOException("Cannot replace report")
                }
                handler.post {
                    if (!destroyed) reportText.text = "本机报告已更新：latest-$kind.json\n保存匿名候选、AP 标记及本次 Direct 接口地址；不包含 MAC、SSID 或设备名称。"
                }
            } catch (error: Exception) {
                handler.post { if (!destroyed) reportText.text = "报告保存失败：${error.javaClass.simpleName}。可重试导出。" }
            } finally {
                temporary?.delete()
            }
        }
    }

    private fun exportReport() {
        exportSnapshot = makeReport("export")
        try {
            @Suppress("DEPRECATION")
            startActivityForResult(Intent(Intent.ACTION_CREATE_DOCUMENT).apply {
                addCategory(Intent.CATEGORY_OPENABLE)
                type = "application/json"
                putExtra(Intent.EXTRA_TITLE, "wanwei-android-${System.currentTimeMillis()}.json")
            }, EXPORT_REQUEST)
        } catch (error: Exception) {
            exportSnapshot = null
            reportText.text = "无法打开系统保存界面：${error.javaClass.simpleName}。本机诊断报告仍可通过调试工具导出。"
        }
    }

    @Deprecated("Uses platform Activity result for this dependency-free lab")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode != EXPORT_REQUEST) return
        val snapshot = exportSnapshot
        exportSnapshot = null
        val uri = data?.data
        if (resultCode != RESULT_OK || uri == null || snapshot == null) {
            reportText.text = "已取消导出；本机诊断报告保留。"
            return
        }
        writer.execute {
            try {
                val stream = contentResolver.openOutputStream(uri, "wt") ?: throw java.io.IOException("No output stream")
                stream.use { it.write(snapshot.toByteArray(Charsets.UTF_8)) }
                handler.post { if (!destroyed) reportText.text = "报告已保存到你选择的位置。" }
            } catch (error: Exception) {
                handler.post { if (!destroyed) reportText.text = "导出失败：${error.javaClass.simpleName}。本机报告仍保留。" }
            }
        }
    }

    private fun buildUi() {
        val scroll = ScrollView(this).apply { setBackgroundColor(bg); isFillViewport = true }
        content = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(22), dp(18), dp(22), dp(32)) }
        scroll.addView(content)
        scroll.setOnApplyWindowInsetsListener { view, insets ->
            if (Build.VERSION.SDK_INT >= 30) {
                val safe = insets.getInsets(WindowInsets.Type.systemBars() or WindowInsets.Type.displayCutout())
                view.setPadding(safe.left, safe.top, safe.right, safe.bottom)
            } else {
                @Suppress("DEPRECATION")
                view.setPadding(insets.systemWindowInsetLeft, insets.systemWindowInsetTop, insets.systemWindowInsetRight, insets.systemWindowInsetBottom)
            }
            insets
        }
        setContentView(scroll)
        addText(content, "WANWEI  /  ANDROID LAB", 12f, accent, true)
        addText(content, "万维 · 直连实验室", 28f, Color.WHITE, true, 10)
        addText(content, "先验证这台手机能做什么。", 15f, muted, false, 8)
        val capabilities = card("01  本机能力")
        capabilityText = addText(capabilities, "正在检查……", 15f, Color.WHITE, false, 12)
        probeButton = button(capabilities, "重新检查能力") { probeCapabilities() }
        val discovery = card("02  附近发现")
        stateText = addText(discovery, "点击扫描，观察系统 Wi-Fi Direct 发现结果。", 15f, Color.WHITE, false, 12)
        scanButton = button(discovery, "扫描附近设备 · 15 秒", true) { requestScan() }
        scanButton.id = R.id.scan_nearby
        stopButton = button(discovery, "停止扫描") { finishScan("cancelled_by_user", "你已取消扫描。") }
        stopButton.id = R.id.stop_scan
        peersText = addText(discovery, "尚未扫描。", 14f, muted, false, 12)
        addText(discovery, "仅显示匿名候选；不会自动连接或配对。离开此页面会结束扫描。", 12f, muted, false, 12)
        val connection = card("03  本次 Direct 建链")
        pairingText = addText(connection, "先扫描并选择候选。电脑需打开“允许本次配对”。", 14f, Color.WHITE, false, 12)
        pairingText.id = R.id.pair_state
        preferPhoneGoCheckBox = CheckBox(this).apply {
            id = R.id.pair_prefer_phone_go
            text = "实验：优先由手机建立直连"
            textSize = 14f
            setTextColor(Color.WHITE)
            buttonTintList = android.content.res.ColorStateList.valueOf(accent)
            isChecked = false
            // Each new page starts with the proven default. The confirmed value is
            // captured below before any asynchronous negotiation begins.
            isSaveEnabled = false
            connection.addView(this, LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(12) })
        }
        addText(connection, "默认关闭。开启后请求由手机建立直连；若对端也要求由它建立，协商可能失败。实际角色和频段以报告为准，不保证 5 GHz，也不代表 Wi-Fi 7 多链路聚合。", 12f, muted, false, 4)
        fileExperimentCheckBox = CheckBox(this).apply {
            id = R.id.pair_file_experiment
            text = "开启 1 MiB 文件实验 · 连接窗口 180 秒"
            textSize = 14f; setTextColor(Color.WHITE)
            buttonTintList = android.content.res.ColorStateList.valueOf(accent)
            isChecked = false; isSaveEnabled = false
            connection.addView(this, LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(12) })
        }
        selectPeerButton = button(connection, "选择附近候选") { choosePeer() }
        selectPeerButton.id = R.id.pair_candidate_select
        connectButton = button(connection, "确认并连接 · 最长 45 秒", true) { requestPairing() }
        connectButton.id = R.id.pair_connect
        cancelPairButton = button(connection, "结束本次连接") { pairing.cancel() }
        cancelPairButton.id = R.id.pair_cancel
        addText(connection, "默认连接后观察 20 秒；明确开启文件实验时最多 180 秒。系统确认窗口遮挡页面时继续有限等待；关闭页面会停止文件任务并请求释放。", 12f, muted, false, 12)
        buildTransferUi()
        val evidence = card("05  诊断记录")
        reportText = addText(evidence, "准备本机报告……", 13f, muted, false, 12)
        exportButton = button(evidence, "导出 JSON 报告") { exportReport() }
        exportButton.id = R.id.export_report
        addText(evidence, "当前版本：0.3.1。P0 只传合成的 1 MiB 测试文件。公钥和监听端口需线下交换，每次收文件必须明确接受；真机互通结果以两端报告为准。", 13f, Color.rgb(242, 205, 142), false, 14)
        button(content, "系统定位设置") { openSettings(Settings.ACTION_LOCATION_SOURCE_SETTINGS) }
        button(content, "应用权限设置") {
            try {
                startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, android.net.Uri.parse("package:$packageName")))
            } catch (_: Exception) { stateText.text = "请在系统设置中打开万维实验室的权限页面。" }
        }
        updateButtons()
    }

    private fun openSettings(action: String) {
        try { startActivity(Intent(action)) }
        catch (_: Exception) { stateText.text = "请手动打开系统设置。" }
    }

    private fun renderCapabilities() {
        if (!::capabilityText.isInitialized) return
        val direct = probe.optJSONObject("wifiDirect") ?: return
        val aware = probe.optJSONObject("wifiAware") ?: JSONObject()
        val environment = probe.optJSONObject("environment") ?: JSONObject()
        val concurrency = probe.optJSONObject("wifiConcurrency") ?: JSONObject()
        capabilityText.text = buildString {
            append("${Build.MANUFACTURER} ${Build.MODEL}\nAndroid ${Build.VERSION.RELEASE} · API ${Build.VERSION.SDK_INT}\n\n")
            append("Wi-Fi Direct\n系统声明：${flag(direct, "hardwareFeatureReported")}  ·  服务：${present(direct, "servicePresent")}\n")
            append("通道：${if (channel != null) "已初始化" else "未就绪"}  ·  运行状态：${when (p2pStateName()) { "enabled" -> "已启用"; "disabled" -> "已关闭"; else -> "未知" }}\n\n")
            append("Wi-Fi Aware\n框架 API：可用  ·  系统声明：${flag(aware, "hardwareFeatureReported")}\n")
            append("运行期：${available(aware, "runtimeAvailable")}  ·  配对：${flag(aware, "pairingSupported")}\n\n")
            append("扫描权限：${if (hasDiscoveryPermission()) "已授予" else "待授权"}  ·  定位开关：${when (locationEnabled()) { true -> "已开启"; false -> "已关闭"; null -> "未知" }}\n")
            append("默认网络为 Wi-Fi：${yesNo(environment, "defaultNetworkWifi")}\n\n")
            append("双 STA 线索\n本地第二连接：${flag(concurrency, "staLocalOnlySupported")}\n双互联网：${flag(concurrency, "staMultiInternetSupported")}\n能力声明不等于 Direct 并发已验证。")
        }
    }

    private fun updateButtons() {
        if (!::scanButton.isInitialized) return
        val idle = !scanning && !stopping && !pairing.busy && !pairing.retryLocked && !DirectPairingSession.radioGate.occupied()
        scanButton.isEnabled = idle
        probeButton.isEnabled = !scanning && !stopping && !pairing.busy
        stopButton.isEnabled = scanning
        exportButton.isEnabled = !scanning && !stopping && !pairing.busy
        selectPeerButton.isEnabled = idle && livePeers.isNotEmpty()
        preferPhoneGoCheckBox.isEnabled = idle
        fileExperimentCheckBox.isEnabled = idle
        connectButton.isEnabled = idle && selectedPeerToken != null
        cancelPairButton.isEnabled = pairing.busy && !pairing.isStopping
        if (::receiveTestButton.isInitialized) {
            val transferring = ::transfer.isInitialized && transfer.busy
            val path = pairing.verifiedTransferPath()
            val trusted = confirmedPeerPin != null && peerPinInput.text.toString().trim().lowercase(Locale.ROOT) == confirmedPeerPin
            trustPeerButton.isEnabled = ::identity.isInitialized && !transferring
            peerPinInput.isEnabled = !transferring
            peerPortInput.isEnabled = !transferring
            remoteAddressInput.isEnabled = !transferring
            receiveTestButton.isEnabled = ::transfer.isInitialized && !transferring && path != null && trusted
            sendTestButton.isEnabled = receiveTestButton.isEnabled
            cancelTransferButton.isEnabled = transferring
            acceptOfferButton.isEnabled = transferring && offerPending
            rejectOfferButton.isEnabled = transferring && offerPending
            if (path != null && !path.localIsOwner && remoteAddressInput.text.isBlank()) remoteAddressInput.setText(path.groupOwnerAddress)
        }
        if (DirectPairingSession.radioGate.locked()) pairingText.text = "上次系统释放未确认，当前进程已锁定新的扫描和连接。请先结束系统 Direct 连接并重新打开应用。"
        else if (DirectPairingSession.radioGate.occupied() && !scanning && !stopping && !pairing.busy)
            pairingText.text = "上一页面的实验仍在清理，请等待结束后重试。"
    }

    private fun choosePeer() {
        if (scanning || stopping || pairing.busy || pairing.retryLocked) return
        val entries = livePeers.entries.toList()
        if (entries.isEmpty()) return
        AlertDialog.Builder(this).setTitle("选择本次连接候选")
            .setItems(entries.mapIndexed { index, entry -> "候选 ${index + 1} · ${entry.key}\n${peerStatusLabel(peerStatus(entry.value.first.status))}" }.toTypedArray()) { _, index ->
                selectedPeerToken = entries[index].key
                selectPeerButton.text = "已选择候选 · ${selectedPeerToken}"
                pairingText.text = "已选择匿名候选 ${selectedPeerToken}。尚未确认应用身份，请核对电脑处于本次配对模式。"
                updateButtons()
            }.setNegativeButton("取消", null).show()
    }

    private fun requestPairing() {
        if (!started || scanning || stopping || pairing.busy || pairing.retryLocked || selectedPeerToken == null) return
        if (!hasDiscoveryPermission() || checkSelfPermission(Manifest.permission.ACCESS_FINE_LOCATION) != PackageManager.PERMISSION_GRANTED) {
            AlertDialog.Builder(this).setTitle("本次连接诊断权限")
                .setMessage("附近设备权限用于 Direct 配对；精确定位权限用于读取当前 Wi-Fi 接入点标识，判断连接过程中原 Wi-Fi 是否变化。程序不读取地理位置，MAC 和 Wi-Fi 名称只在内存中转换为本次匿名标记。拒绝精确定位仍可建链，但原接入点将记录为未知。")
                .setPositiveButton("继续并请求权限") { _, _ ->
                    pendingPairPermission = true
                    val permissions = mutableListOf(Manifest.permission.ACCESS_COARSE_LOCATION, Manifest.permission.ACCESS_FINE_LOCATION)
                    if (Build.VERSION.SDK_INT >= 33) permissions.add(Manifest.permission.NEARBY_WIFI_DEVICES)
                    requestPermissions(permissions.toTypedArray(), PAIR_PERMISSION_REQUEST)
                }.setNegativeButton("取消", null).show()
        } else confirmPairing()
    }

    private fun confirmPairing() {
        if (!started || pairing.busy || scanning || stopping || pairing.retryLocked) return
        val token = selectedPeerToken ?: return
        val selected = livePeers[token] ?: return
        if (locationEnabled() == false) { pairingText.text = "系统定位开关关闭，请先开启再试；否则候选和 AP 诊断可能不可用。"; return }
        val preferPhoneGroupOwner = preferPhoneGoCheckBox.isChecked
        val fileExperiment = fileExperimentCheckBox.isChecked
        val usbLabExperiment = usbLabNonce != null && SystemClock.elapsedRealtime() < usbLabExpiresAtMs
        val roleSummary = if (preferPhoneGroupOwner)
            "本次实验：优先由手机建立直连。若对端也要求由它建立，协商可能失败。实际角色和频段以报告为准，不保证 5 GHz，也不代表多链路聚合。"
        else "本次模式：系统默认协商两端角色和频段。"
        AlertDialog.Builder(this).setTitle("连接这个附近候选？")
            .setMessage("候选 $token\n$roleSummary\n请先在电脑打开“等待连接请求”。本次请求系统 Direct 连接并观察原 Wi-Fi；${if (fileExperiment) "文件实验窗口为 180 秒，收发仍需另行确认完整公钥和每次文件请求。" else "观察窗口为 20 秒。"}若已有 Direct 组，将保留原组并拒绝本次实验。")
            .setPositiveButton("确认连接") { _, _ ->
                if (started && !pairing.busy && !scanning && !stopping && hasDiscoveryPermission()) {
                    pairing.start(selected.first, token, selected.second, preferPhoneGroupOwner, fileExperiment, usbLabExperiment)
                    updateButtons()
                }
            }.setNegativeButton("取消", null).show()
    }

    private fun prepareIdentity() {
        val worker = Executors.newSingleThreadExecutor()
        worker.execute {
            try {
                val prepared = P0Identity()
                val report = JSONObject().put("schema", "wanwei-public-identity/0.1").put("appVersion", "0.3.1")
                    .put("identity", prepared.fingerprint).put("identityEncoding", "sha256_der_subject_public_key_info_lowercase_hex")
                    .put("keyStore", "AndroidKeyStore_non_exportable_private_key")
                    .put("certificateDerBase64", android.util.Base64.encodeToString(prepared.certificate.encoded, android.util.Base64.NO_WRAP))
                val directory = File(filesDir, "reports").apply { mkdirs() }
                FileOutputStream(File(directory, "public-identity.json")).use { it.write(report.toString(2).toByteArray()); it.fd.sync() }
                handler.post {
                    if (!destroyed) {
                        identity = prepared
                        transfer = P0TransferSession(this, identity, pairing::verifiedTransferPath) { update ->
                            handler.post {
                                if (!destroyed) {
                                    transferStateText.text = update.message
                                    offerPending = update.offerPending
                                    updateButtons()
                                }
                            }
                        }
                        localPinText.text = "本机完整公钥指纹（64 位）：\n${identity.fingerprint}"
                        confirmedPeerPin = getSharedPreferences("p0-trust", MODE_PRIVATE).getString("peerPin", null)
                        confirmedPeerPin?.let(peerPinInput::setText)
                        updateButtons()
                    }
                }
            } catch (error: Exception) {
                handler.post { if (!destroyed) { transferStateText.text = "身份准备失败：${error.javaClass.simpleName}。文件实验保持关闭。"; updateButtons() } }
            } finally { worker.shutdown() }
        }
    }

    private fun buildTransferUi() {
        val card = card("04  安全文件实验 · 1 MiB")
        usbLabStateText = addText(card, "普通手动确认模式。", 13f, accent, true, 12).apply { id = R.id.usb_lab_state }
        localPinText = addText(card, "正在准备本机受保护的应用身份……", 13f, Color.WHITE, false, 12).apply {
            id = R.id.transfer_local_pin; setTextIsSelectable(true)
        }
        addText(card, "通过当面比对或已授权 USB 调试渠道，交换双方完整 64 位指纹，再确认信任。端口也要线下交给对方；这一步目前是实验操作。", 12f, muted, false, 8)
        fun input(idValue: Int, hintValue: String, numeric: Boolean = false): EditText = EditText(this).apply {
            id = idValue; hint = hintValue; textSize = 14f; setTextColor(Color.WHITE); setHintTextColor(muted)
            inputType = if (numeric) android.text.InputType.TYPE_CLASS_NUMBER else android.text.InputType.TYPE_CLASS_TEXT or android.text.InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS
            isSaveEnabled = false
            card.addView(this, LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(8) })
        }
        peerPinInput = input(R.id.transfer_peer_pin, "对方完整公钥指纹（64 位）")
        trustPeerButton = button(card, "核对并确认这个公钥") {
            val pin = peerPinInput.text.toString().trim().lowercase(Locale.ROOT)
            if (!P0Protocol.fingerprint.matches(pin)) { transferStateText.text = "请输入完整 64 位十六进制公钥指纹。"; return@button }
            if (::identity.isInitialized && pin == identity.fingerprint) { transferStateText.text = "这是本机公钥，请输入对方页面显示的完整公钥指纹。"; return@button }
            AlertDialog.Builder(this).setTitle("确认线下核对的对端公钥")
                .setMessage("请核对对端本机页面或已授权 USB 渠道导出的完整指纹：\n$pin\n确认后只允许持有这个公钥对应私钥的对端完成 TLS 认证。")
                .setPositiveButton("已核对，确认信任") { _, _ ->
                    confirmedPeerPin = pin
                    getSharedPreferences("p0-trust", MODE_PRIVATE).edit().putString("peerPin", pin).apply()
                    transferStateText.text = "公钥已确认。先开启文件实验并建链，再选择发送或等待接收。"
                    updateButtons()
                }.setNegativeButton("取消", null).show()
        }.apply { id = R.id.transfer_trust_peer }
        remoteAddressInput = input(R.id.transfer_remote_address, "对方本次直连 IPv4 地址")
        peerPortInput = input(R.id.transfer_peer_port, "对方本次接收端口", true)
        transferStateText = addText(card, "先开启文件实验、建立 Direct 并核对完整公钥。", 14f, Color.WHITE, false, 12).apply { id = R.id.transfer_state }
        receiveTestButton = button(card, "等待接收 1 MiB 测试文件", true) {
            if (::transfer.isInitialized && confirmedPeerPin != null) transfer.startReceiver(confirmedPeerPin!!)
            updateButtons()
        }.apply { id = R.id.transfer_listen }
        sendTestButton = button(card, "发送 1 MiB 测试文件", true) {
            val port = peerPortInput.text.toString().toIntOrNull()
            if (port == null || port !in 1..65535) { transferStateText.text = "请输入对方页面显示的本次接收端口。"; return@button }
            if (::transfer.isInitialized && confirmedPeerPin != null) transfer.startSender(confirmedPeerPin!!, remoteAddressInput.text.toString().trim(), port)
            updateButtons()
        }.apply { id = R.id.transfer_send }
        acceptOfferButton = button(card, "接受本次 1 MiB 测试文件", true) { offerPending = false; transfer.acceptOffer(true); updateButtons() }.apply { id = R.id.transfer_accept }
        rejectOfferButton = button(card, "拒绝本次测试文件") { offerPending = false; transfer.acceptOffer(false); updateButtons() }.apply { id = R.id.transfer_reject }
        cancelTransferButton = button(card, "取消本次文件实验") { transfer.cancel(); updateButtons() }.apply { id = R.id.transfer_cancel }
    }

    private fun activateUsbLabIfRequested() {
        val requestedNonce = intent?.getStringExtra("wanwei_lab_nonce") ?: return
        if (!::identity.isInitialized || !::transfer.isInitialized || confirmedPeerPin == null) return
        intent.removeExtra("wanwei_lab_nonce") // No implicit reactivation after onStop or another onStart.
        if (usbLabNonce != null || pairing.busy || consumedLabNonces.contains(requestedNonce)) {
            writeUsbLabStatus("activation_rejected", "busy_or_nonce_replayed")
            return
        }
        try {
            if ((applicationInfo.flags and android.content.pm.ApplicationInfo.FLAG_DEBUGGABLE) == 0) P0Protocol.fail("INVALID_MESSAGE")
            val file = File(filesDir, "p0-lab-bootstrap.json")
            val bytes = readPrivateLabFile(file)
            android.system.Os.remove(file.path)
            val bootstrap = P0UsbLabPolicy.bootstrap(bytes, requestedNonce, confirmedPeerPin!!)
            if (bootstrap.peerPin == identity.fingerprint) P0Protocol.fail("IDENTITY_MISMATCH")
            consumedLabNonces.add(bootstrap.nonce)
            usbLabNonce = bootstrap.nonce; usbLabPeerPin = bootstrap.peerPin
            usbLabExpiresAtMs = SystemClock.elapsedRealtime() + 600_000
            consumedLabCommandIds.clear()
            fileExperimentCheckBox.isChecked = true
            usbLabStateText.text = "USB 辅助固定数据实验已启用：仅已核对的对端、1 MiB 合成文件；600 秒到期或离开页面停止。"
            writeUsbLabStatus("active", "explicit_debug_private_bootstrap")
            updateButtons()
        } catch (error: Exception) {
            writeUsbLabStatus("activation_rejected", if (error is P0Protocol.Failure) error.code else error.javaClass.simpleName)
        }
    }

    private fun deactivateUsbLab(reason: String) {
        if (usbLabNonce == null) return
        if (::transfer.isInitialized) transfer.cancel("CANCELLED")
        writeUsbLabStatus("disabled", reason)
        usbLabNonce = null; usbLabPeerPin = null; usbLabExpiresAtMs = 0L
        if (::usbLabStateText.isInitialized) usbLabStateText.text = "USB 固定数据实验已停止；恢复普通手动确认模式。"
    }

    private fun consumeUsbLabCommand() {
        val nonce = usbLabNonce ?: return
        val file = File(filesDir, "p0-lab-command.json")
        if (!file.exists()) return
        var command: P0UsbLabPolicy.Command? = null
        try {
            val bytes = readPrivateLabFile(file)
            android.system.Os.remove(file.path) // Consume BEFORE executing; failed commands cannot replay.
            command = P0UsbLabPolicy.command(bytes, nonce)
            if (consumedLabCommandIds.size >= 128 || !consumedLabCommandIds.add(command.commandId)) P0Protocol.fail("INVALID_MESSAGE")
            if (confirmedPeerPin != usbLabPeerPin || peerPinInput.text.toString().trim().lowercase(Locale.ROOT) != usbLabPeerPin) P0Protocol.fail("IDENTITY_MISMATCH")
            val pin = usbLabPeerPin ?: P0Protocol.fail("IDENTITY_MISMATCH")
            when (command.action) {
                "cancel" -> transfer.cancel("CANCELLED")
                "acceptfixedfixture" -> {
                    if (pairing.verifiedTransferPath() == null || !transfer.acceptFixedFixtureByOperator()) P0Protocol.fail("UNEXPECTED_MESSAGE")
                }
                "receive", "send" -> {
                    if (transfer.busy) P0Protocol.fail("UNEXPECTED_MESSAGE")
                    if (pairing.verifiedTransferPath() == null) P0Protocol.fail("DIRECT_PATH_UNVERIFIED")
                    if (command.action == "receive") transfer.startReceiver(pin, operatorPreauthorized = true)
                    else transfer.startSender(pin, command.peerAddress!!, command.port!!, operatorPreauthorized = true)
                }
            }
            writeUsbLabStatus("command_executed", "accepted_for_bounded_fixture_task", command)
            updateButtons()
        } catch (error: Exception) {
            runCatching { if (file.exists()) android.system.Os.remove(file.path) }
            writeUsbLabStatus("command_rejected", if (error is P0Protocol.Failure) error.code else error.javaClass.simpleName, command)
        }
    }

    private fun readPrivateLabFile(file: File): ByteArray {
        if (Build.VERSION.SDK_INT < 29) P0Protocol.fail("INVALID_MESSAGE") // P0 requires platform TLS 1.3.
        val fd = android.system.Os.open(file.path, android.system.OsConstants.O_RDONLY or android.system.OsConstants.O_NOFOLLOW or android.system.OsConstants.O_CLOEXEC, 0)
        try {
            val stat = android.system.Os.fstat(fd)
            if (!android.system.OsConstants.S_ISREG(stat.st_mode) || stat.st_size !in 1..P0UsbLabPolicy.MAXIMUM_BYTES.toLong()) P0Protocol.fail("INVALID_MESSAGE")
            val bytes = ByteArray(P0UsbLabPolicy.MAXIMUM_BYTES + 1); var count = 0
            while (count < bytes.size) {
                val read = android.system.Os.read(fd, bytes, count, bytes.size - count)
                if (read == 0) break
                count += read
            }
            if (count !in 1..P0UsbLabPolicy.MAXIMUM_BYTES) P0Protocol.fail("INVALID_MESSAGE")
            return bytes.copyOf(count)
        } finally { android.system.Os.close(fd) }
    }

    private fun writeUsbLabStatus(state: String, reason: String, command: P0UsbLabPolicy.Command? = null) {
        val nonce = usbLabNonce
        val token = nonce?.let { MessageDigest.getInstance("SHA-256").digest(it.toByteArray(Charsets.US_ASCII)).joinToString("") { byte -> "%02x".format(byte.toInt() and 255) } }
        val report = JSONObject().put("schema", P0UsbLabPolicy.SCHEMA).put("state", state).put("reason", reason)
            .put("updatedAtUtc", Instant.now().toString()).put("sessionToken", token ?: JSONObject.NULL)
            .put("commandId", command?.commandId ?: JSONObject.NULL).put("action", command?.action ?: JSONObject.NULL)
            .put("fixture", P0Protocol.FIXTURE).put("operatorFixturePreauthorized", nonce != null && state != "disabled")
            .put("nativeFileExperimentWindowMs", 600_000).put("fileSessionAbsoluteLimitMs", 180_000)
            .put("commandsCannotChangeRadioOrSettings", true)
        runCatching {
            val directory = File(filesDir, "reports").apply { mkdirs() }
            val temporary = File(directory, "lab-status.json.tmp")
            FileOutputStream(temporary).use { it.write(report.toString(2).toByteArray(Charsets.UTF_8)); it.fd.sync() }
            if (!temporary.renameTo(File(directory, "lab-status.json"))) throw java.io.IOException("lab_status_commit_failed")
        }
    }

    private fun card(title: String): LinearLayout = LinearLayout(this).apply {
        orientation = LinearLayout.VERTICAL
        setPadding(dp(18), dp(18), dp(18), dp(18))
        background = GradientDrawable().apply { setColor(panel); cornerRadius = dp(18).toFloat() }
        content.addView(this, LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(22) })
        addText(this, title, 13f, accent, true)
    }

    private fun addText(parent: LinearLayout, value: String, size: Float, color: Int, bold: Boolean = false, top: Int = 0): TextView = TextView(this).apply {
        text = value; textSize = size; setTextColor(color); setLineSpacing(dp(3).toFloat(), 1f)
        if (bold) setTypeface(typeface, Typeface.BOLD)
        parent.addView(this, LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(top) })
    }

    private fun button(parent: LinearLayout, title: String, primary: Boolean = false, action: () -> Unit): Button = Button(this).apply {
        text = title; isAllCaps = false; textSize = 14f; minHeight = dp(50)
        setTextColor(if (primary) bg else Color.WHITE)
        backgroundTintList = android.content.res.ColorStateList(
            arrayOf(intArrayOf(-android.R.attr.state_enabled), intArrayOf()),
            intArrayOf(Color.rgb(43, 51, 63), if (primary) accent else Color.rgb(46, 58, 76)))
        parent.addView(this, LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(10) })
        setOnClickListener { action() }
    }

    private fun dp(value: Int) = (value * resources.displayMetrics.density + .5f).toInt()
    private fun flag(json: JSONObject, key: String) = if (!json.has(key) || json.isNull(key)) "未知" else if (json.optBoolean(key)) "支持" else "未报告支持"
    private fun present(json: JSONObject, key: String) = if (!json.has(key) || json.isNull(key)) "未知" else if (json.optBoolean(key)) "存在" else "未提供"
    private fun available(json: JSONObject, key: String) = if (!json.has(key) || json.isNull(key)) "未知" else if (json.optBoolean(key)) "可用" else "不可用"
    private fun yesNo(json: JSONObject, key: String) = if (!json.has(key) || json.isNull(key)) "未知" else if (json.optBoolean(key)) "是" else "否"
    private fun failureName(reason: Int) = when (reason) {
        WifiP2pManager.P2P_UNSUPPORTED -> "系统不支持（$reason）"
        WifiP2pManager.BUSY -> "系统正忙（$reason）"
        WifiP2pManager.ERROR -> "系统错误（$reason）"
        else -> "错误代码 $reason"
    }
    private fun peerStatus(value: Int) = when (value) {
        WifiP2pDevice.CONNECTED -> "connected_platform_status"
        WifiP2pDevice.INVITED -> "invited_platform_status"
        WifiP2pDevice.FAILED -> "failed_platform_status"
        WifiP2pDevice.AVAILABLE -> "available_platform_status"
        WifiP2pDevice.UNAVAILABLE -> "unavailable_platform_status"
        else -> "unknown"
    }
    private fun peerStatusLabel(value: String) = when (value) {
        "available_platform_status" -> "系统报告可用"
        "connected_platform_status" -> "系统报告已连接（非本应用建立）"
        "invited_platform_status" -> "系统报告已邀请（非本应用发起）"
        "failed_platform_status" -> "系统报告失败"
        "unavailable_platform_status" -> "系统报告不可用"
        else -> "状态未知"
    }

    companion object {
        private const val PERMISSION_REQUEST = 101
        private const val PAIR_PERMISSION_REQUEST = 103
        private const val EXPORT_REQUEST = 102
        private const val PENDING_EXPORT_STATE = "pending_export_snapshot"
        // Shared for all Activity instances; shutdown/draining during rotation must not race the next instance.
        private val reportWriter = Executors.newSingleThreadExecutor { task ->
            Thread(task, "wanwei-report-io")
        }
    }
}
