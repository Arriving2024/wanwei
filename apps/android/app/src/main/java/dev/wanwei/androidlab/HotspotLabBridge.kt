package dev.wanwei.androidlab

import android.os.Handler
import android.os.Looper
import org.json.JSONObject
import java.util.concurrent.FutureTask
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference

/** Debug-only handoff in memory; no network credentials enter reports or files. */
internal object HotspotLabBridge {
    private val session = AtomicReference<LocalHotspotSession?>(null)
    private val main = Handler(Looper.getMainLooper())
    fun attach(value: LocalHotspotSession) { session.set(value) }
    fun detach(value: LocalHotspotSession) { session.compareAndSet(value, null) }
    fun credentials(): JSONObject? {
        if (!BuildConfig.DEBUG) return null
        if (Looper.myLooper() == Looper.getMainLooper()) return session.get()?.debugCredentials()
        val read = FutureTask { session.get()?.debugCredentials() }
        if (!main.post(read)) return null
        return try { read.get(1, TimeUnit.SECONDS) }
        catch (_: Exception) { read.cancel(false); null }
    }
}
