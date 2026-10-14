package dev.wanwei.androidlab

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.net.ConnectivityManager
import android.net.LinkProperties
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.wifi.WifiInfo
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Handler
import android.os.SystemClock
import org.json.JSONArray
import org.json.JSONObject
import java.security.SecureRandom
import java.time.Instant
import java.util.Locale
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/** Passive observation only. Never requests a network, binds the process, or probes the Internet. */
internal class WifiObservation(private val context: Context, private val handler: Handler) {
    private val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
    private val key = ByteArray(32).also { SecureRandom().nextBytes(it) }
    private val samples = JSONArray()
    private val events = JSONArray()
    private val baseline = linkedSetOf<Network>()
    private val callbackCapabilities = linkedMapOf<Network, NetworkCapabilities>()
    private val interfaces = linkedMapOf<Network, String?>()
    private var active = false
    private var registered = false
    private var initialAp: String? = null
    private var lost = false
    private var comparable = 0
    private var unknown = 0
    private var changed = 0
    private var callbackApChanged = false
    private var maxGap = 0L
    private var previousSample = 0L
    private var started = 0L
    private val value = JSONObject().put("requestedSampleIntervalMs", 1_000)
        .put("internetTrafficTestExecuted", false)
        .put("continuousInternetPreservationVerified", false)
        .put("rawSsidStored", false).put("rawBssidStored", false)
        .put("identityTokenScope", "HMAC-SHA256 96 bits, random key for this pairing attempt, never persisted")
        .put("notes", "All visible Wi-Fi networks are observed, independent of the default network. Sampling and system events do not prove uninterrupted Internet traffic. Missing/redacted AP identity remains unknown.")
    private val callback: ConnectivityManager.NetworkCallback =
        if (Build.VERSION.SDK_INT >= 31) object : ConnectivityManager.NetworkCallback(ConnectivityManager.NetworkCallback.FLAG_INCLUDE_LOCATION_INFO) {
            override fun onAvailable(network: Network) = available(network)
            override fun onLost(network: Network) = gone(network)
            override fun onCapabilitiesChanged(network: Network, caps: NetworkCapabilities) = capabilities(network, caps)
            override fun onLinkPropertiesChanged(network: Network, links: LinkProperties) = links(network, links)
            override fun onLosing(network: Network, maxMsToLive: Int) { event("losing", network).put("maxMsToLive", maxMsToLive) }
        } else object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) = available(network)
            override fun onLost(network: Network) = gone(network)
            override fun onCapabilitiesChanged(network: Network, caps: NetworkCapabilities) = capabilities(network, caps)
            override fun onLinkPropertiesChanged(network: Network, links: LinkProperties) = links(network, links)
        }

    fun start() {
        active = true
        started = SystemClock.elapsedRealtime()
        value.put("startedAtUtc", Instant.now().toString())
        sample("before")
        try {
            cm.registerNetworkCallback(NetworkRequest.Builder().addTransportType(NetworkCapabilities.TRANSPORT_WIFI).build(), callback, handler)
            registered = true
            value.put("networkCallbackRegistered", true)
        } catch (error: Exception) {
            value.put("networkCallbackRegistered", false).put("callbackErrorType", error.javaClass.simpleName)
        }
        value.put("baselineNetworkCount", baseline.size)
            .put("baselineApIdentityState", if (initialAp == null) "unknown" else "observed")
            .put("baselineApToken", initialAp ?: JSONObject.NULL)
    }

    fun sample(stage: String) {
        if (!active) return
        val now = SystemClock.elapsedRealtime()
        if (previousSample != 0L) maxGap = maxOf(maxGap, now - previousSample)
        previousSample = now
        val wifiNetworks = JSONArray()
        try {
            @Suppress("DEPRECATION") val networks = cm.allNetworks
            networks.forEach { network ->
                val caps = cm.getNetworkCapabilities(network)
                if (caps?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) == true) {
                    if (stage == "before") baseline.add(network)
                    val entry = describe(network, callbackCapabilities[network] ?: caps)
                    entry.put("interfaceName", cm.getLinkProperties(network)?.interfaceName ?: JSONObject.NULL)
                    wifiNetworks.put(entry)
                }
            }
        } catch (error: Exception) { value.put("networkEnumerationErrorType", error.javaClass.simpleName) }
        val primary = primaryWifiInfo()
        val token = primary.optString("bssidToken").takeIf { primary.has("bssidToken") && !primary.isNull("bssidToken") }
        if (stage == "before") initialAp = token
        else if (initialAp == null || token == null) unknown++
        else { comparable++; if (token != initialAp) changed++ }
        if (samples.length() < 160) samples.put(JSONObject().put("stage", stage)
            .put("atUtc", Instant.now().toString()).put("elapsedMs", now - started)
            .put("wifiNetworks", wifiNetworks).put("primaryWifiManagerObservation", primary))
        else value.put("sampleLimitReached", true)
    }

    fun stop(): JSONObject {
        if (active) sample("after")
        active = false
        if (registered) {
            try { cm.unregisterNetworkCallback(callback); value.put("networkCallbackUnregistered", true) }
            catch (error: Exception) { value.put("unregisterErrorType", error.javaClass.simpleName) }
            registered = false
        }
        value.put("finishedAtUtc", Instant.now().toString())
        return snapshot()
    }

    fun snapshot(): JSONObject = value.put("samples", samples).put("networkEvents", events)
        .put("sampleCount", samples.length()).put("eventCount", events.length()).put("maxSampleGapMs", maxGap)
        .put("baselineWifiNetworkLossEventObserved", lost).put("comparableApSamples", comparable)
        .put("unknownApSamples", unknown).put("changedApSamples", changed)
        .put("baselineNetworkApChangeEventObserved", callbackApChanged)
        .put("originalWifiObservation", if (!value.optBoolean("networkCallbackRegistered")) "unknown_callback_not_registered" else
            PairingSafety.preservationSummary(initialAp != null && baseline.size == 1, comparable, unknown, changed + if (callbackApChanged) 1 else 0, lost, maxGap > 2_000))

    private fun available(network: Network) { if (active) event("available", network) }
    private fun gone(network: Network) {
        if (!active) return
        if (baseline.contains(network)) lost = true
        callbackCapabilities.remove(network); interfaces.remove(network)
        event("lost", network).put("wasBaselineWifiNetwork", baseline.contains(network))
    }
    private fun capabilities(network: Network, caps: NetworkCapabilities) {
        if (!active) return
        callbackCapabilities[network] = caps
        if (baseline.size == 1 && baseline.contains(network) && initialAp != null && Build.VERSION.SDK_INT >= 29) {
            val identity = wifiIdentity(caps.transportInfo as? WifiInfo)
            if (identity.optString("state") == "observed" && identity.optString("bssidToken") != initialAp) callbackApChanged = true
        }
        event("capabilities_changed", network).put("capabilities", describe(network, caps))
    }
    private fun links(network: Network, links: LinkProperties) {
        if (!active) return
        interfaces[network] = links.interfaceName
        event("link_properties_changed", network).put("interfaceName", links.interfaceName ?: JSONObject.NULL)
    }
    private fun event(kind: String, network: Network): JSONObject {
        val entry = JSONObject().put("kind", kind).put("atUtc", Instant.now().toString())
            .put("elapsedMs", SystemClock.elapsedRealtime() - started).put("networkToken", token(network.toString()))
        if (events.length() < 600) events.put(entry) else value.put("eventLimitReached", true)
        return entry
    }
    private fun describe(network: Network, caps: NetworkCapabilities): JSONObject = JSONObject()
        .put("networkToken", token(network.toString())).put("wasBaselineWifiNetwork", baseline.contains(network))
        .put("internetCapability", caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET))
        .put("systemValidated", caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED))
        .put("apIdentity", if (Build.VERSION.SDK_INT >= 29) wifiIdentity(caps.transportInfo as? WifiInfo) else JSONObject().put("state", "unknown_requires_api_29"))

    @Suppress("DEPRECATION")
    private fun primaryWifiInfo(): JSONObject = try {
        val wifi = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager
        wifiIdentity(wifi?.connectionInfo).put("source", "WifiManager.connectionInfo_primary_only")
            .put("multipleStaNetworkAttribution", "unknown_use_network_callback_entries")
    } catch (error: Exception) { JSONObject().put("state", "unknown").put("errorType", error.javaClass.simpleName) }

    private fun wifiIdentity(info: WifiInfo?): JSONObject {
        val entry = JSONObject().put("state", "unknown").put("ssidToken", JSONObject.NULL).put("bssidToken", JSONObject.NULL)
        if (info == null) return entry.put("reason", "transport_info_unavailable")
        entry.put("frequencyMhz", info.frequency).put("rssiDbm", info.rssi).put("linkSpeedMbps", info.linkSpeed)
        if (context.checkSelfPermission(Manifest.permission.ACCESS_FINE_LOCATION) != PackageManager.PERMISSION_GRANTED)
            return entry.put("reason", "fine_location_permission_not_granted")
        val ssid = info.ssid?.trim('"').orEmpty()
        val bssid = info.bssid.orEmpty()
        if (ssid.isNotBlank() && ssid != WifiManager.UNKNOWN_SSID && ssid != "<unknown ssid>") entry.put("ssidToken", token("ssid:$ssid"))
        if (PairingSafety.isUsableBssid(bssid)) {
            entry.put("bssidToken", token("bssid:$bssid")).put("state", "observed")
        } else entry.put("reason", "bssid_redacted_or_no_association")
        return entry
    }
    private fun token(input: String): String {
        val mac = Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(key, "HmacSHA256")) }
        return mac.doFinal(input.lowercase(Locale.ROOT).toByteArray(Charsets.UTF_8)).take(12)
            .joinToString("") { "%02x".format(it.toInt() and 255) }
    }
}
