package dev.wanwei.androidlab

import android.content.ContentProvider
import android.content.ContentValues
import android.content.pm.ApplicationInfo
import android.database.Cursor
import android.database.MatrixCursor
import android.net.Uri
import android.os.Binder
import android.util.Base64

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
    override fun insert(uri: Uri, values: ContentValues?): Uri? = throw UnsupportedOperationException("Read only")
    override fun delete(uri: Uri, selection: String?, selectionArgs: Array<out String>?): Int = throw UnsupportedOperationException("Read only")
    override fun update(uri: Uri, values: ContentValues?, selection: String?, selectionArgs: Array<out String>?): Int = throw UnsupportedOperationException("Read only")
}
