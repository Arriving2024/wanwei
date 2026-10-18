package dev.wanwei.androidlab

/** Process lifetime ownership: Activity recreation cannot bypass an in-flight or uncertain cleanup. */
internal class RadioSessionGate {
    private var owner: String? = null
    private var uncertain = false
    @Synchronized fun acquire(value: String): Boolean {
        if (owner != null || uncertain) return false
        owner = value
        return true
    }
    @Synchronized fun release(value: String, cleanupUncertain: Boolean): Boolean {
        if (owner != value) return false
        if (cleanupUncertain) uncertain = true
        owner = null
        return true
    }
    @Synchronized fun occupied(): Boolean = owner != null
    @Synchronized fun locked(): Boolean = uncertain
    /** A late callback can taint the process without taking or releasing another owner's lease. */
    @Synchronized fun latchUncertain() { uncertain = true }
}
