package dev.wanwei.androidlab

import android.content.ContentProvider
import android.content.ContentValues
import android.content.pm.ApplicationInfo
import android.database.Cursor
import android.database.MatrixCursor
import android.net.Uri
import android.os.Binder
import android.os.Bundle
import android.util.Base64
import org.json.JSONObject
import java.nio.ByteBuffer
import java.nio.charset.CodingErrorAction

/** Only the owner-authorized ADB shell can read an active lab lease's credentials.
 * Declared solely in the debug manifest. Values live in the Binder/stdout exchange,
 * never a private bootstrap file, intent, diagnostic report or log message.
 */
class HotspotLabProvider : ContentProvider() {
    override fun onCreate(): Boolean = BuildConfig.DEBUG &&
        (context?.applicationInfo?.flags?.and(ApplicationInfo.FLAG_DEBUGGABLE) ?: 0) != 0

    override fun query(uri: Uri, projection: Array<out String>?, selection: String?,
                       selectionArgs: Array<out String>?, sortOrder: String?): Cursor {
        val debug = context?.applicationInfo?.flags?.and(ApplicationInfo.FLAG_DEBUGGABLE) ?: 0
        if (!BuildConfig.DEBUG || debug == 0 || Binder.getCallingUid() != 2000)
            throw SecurityException("Owner-authorized debug shell required")
        if (uri.path != "/session" || projection != null || selection != null || selectionArgs != null || sortOrder != null)
            throw IllegalArgumentException("Unsupported lab request")
        val credentials = HotspotLabBridge.credentials()
            ?: throw IllegalStateException("No active local Wi-Fi lab lease")
        val encoded = Base64.encodeToString(credentials.toString().toByteArray(Charsets.UTF_8), Base64.NO_WRAP)
        return MatrixCursor(arrayOf("payload64"), 1).apply { addRow(arrayOf(encoded)) }
    }

    override fun getType(uri: Uri): String = "vnd.android.cursor.item/vnd.wanwei.hotspot-lab"
    override fun call(method: String, arg: String?, extras: Bundle?): Bundle {
        val debug = context?.applicationInfo?.flags?.and(ApplicationInfo.FLAG_DEBUGGABLE) ?: 0
        if (!BuildConfig.DEBUG || debug == 0 || Binder.getCallingUid() != 2000)
            throw SecurityException("Owner-authorized debug shell required")
        // Android's `content call` supplies an empty Bundle even without --extra.
        // No side channel is accepted: any actual extra field is still rejected.
        if (method != "fixture" || (extras != null && !extras.isEmpty) || arg == null || arg.length !in 1..21_848 ||
            !arg.matches(Regex("[A-Za-z0-9+/]+={0,2}"))) throw IllegalArgumentException("Unsupported lab request")
        val decoded = try { Base64.decode(arg, Base64.NO_WRAP) } catch (_: Exception) { throw IllegalArgumentException("Invalid lab payload") }
        if (decoded.size > 16_384) throw IllegalArgumentException("Lab payload too large")
        val raw = try { Charsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT)
            .onUnmappableCharacter(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(decoded)).toString() }
        catch (_: Exception) { throw IllegalArgumentException("Invalid lab encoding") }
        if (!LocalHotspotFixturePolicy.strictRequestShape(raw)) throw IllegalArgumentException("Invalid lab JSON")
        val request = try { JSONObject(raw) } catch (_: Exception) { throw IllegalArgumentException("Invalid lab JSON") }
        // Commands contain only primitive bounded fields. Reject unknown, nested, duplicate and oversized values.
        if (request.length() > 10 || request.keys().asSequence().any { key ->
                key.length > 32 || request.opt(key).let { it !is String && it !is Boolean } ||
                    (request.opt(key) as? String)?.let { it.length > 128 || it.any { c -> c.code !in 32..126 || c == '"' || c == '\\' } } == true
            } || Regex("\"[^\"\\\\]*\"\\s*:").findAll(raw).count() != request.length())
            throw IllegalArgumentException("Invalid lab fields")
        val response = HotspotLabBridge.debugCall(request)
        val encoded = Base64.encodeToString(response.toString().toByteArray(Charsets.UTF_8), Base64.NO_WRAP)
        return Bundle().apply { putString("payload64", encoded) }
    }
    override fun insert(uri: Uri, values: ContentValues?): Uri? = throw UnsupportedOperationException("Read only")
    override fun delete(uri: Uri, selection: String?, selectionArgs: Array<out String>?): Int = throw UnsupportedOperationException("Read only")
    override fun update(uri: Uri, values: ContentValues?, selection: String?, selectionArgs: Array<out String>?): Int = throw UnsupportedOperationException("Read only")
}
