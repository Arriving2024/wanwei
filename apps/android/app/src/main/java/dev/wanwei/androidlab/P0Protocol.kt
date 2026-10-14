package dev.wanwei.androidlab

import java.io.EOFException
import java.io.InputStream
import java.io.OutputStream
import java.nio.ByteBuffer
import java.nio.charset.CodingErrorAction

/** Deliberately small strict parser for the flat P0 control objects. No Android dependency. */
internal object P0Protocol {
    const val SIZE = 1_048_576L
    const val HASH = "fbbab289f7f94b25736c58be46a994c441fd02552cc6022352e3d86d2fab7c83"
    const val FIXTURE = "ramp256-1mib-v1"
    val fingerprint = Regex("[0-9a-f]{64}")
    val transferId = Regex("[0-9a-f]{32}")
    val errors = setOf("UNSUPPORTED_VERSION", "UNSUPPORTED_SERVICE", "INVALID_MESSAGE", "INVALID_FRAME", "UNEXPECTED_MESSAGE", "IDENTITY_MISMATCH", "SIZE_MISMATCH", "HASH_MISMATCH", "STORAGE_FAILED", "CANCELLED", "TIMEOUT", "DIRECT_PATH_UNVERIFIED")
    class Failure(val code: String) : Exception(code)
    class PeerFailure(val code: String) : Exception(code)
    data class Frame(val kind: Int, val payload: ByteArray)
    data class Control(val values: Map<String, Any?>) {
        val type: String get() = values["type"] as String
        fun string(key: String): String = values[key] as String
        fun number(key: String): Long = values[key] as Long
    }
    fun parse(bytes: ByteArray): Control {
        if (bytes.isEmpty() || bytes.size > 4096) fail("INVALID_FRAME")
        val values = parseFlatObject(bytes, 4096)
        val type = values["type"] as? String ?: fail("INVALID_MESSAGE")
        val fields = when (type) {
            "HELLO" -> setOf("type", "version", "role", "service", "identity")
            "OFFER" -> setOf("type", "transferId", "fixture", "sizeBytes", "sha256")
            "ACCEPT" -> setOf("type", "transferId")
            "REJECT" -> setOf("type", "transferId", "reason")
            "COMPLETE" -> setOf("type", "transferId", "sizeBytes", "sha256")
            "ERROR" -> setOf("type", "transferId", "code")
            else -> fail("INVALID_MESSAGE")
        }
        if (values.keys != fields) fail("INVALID_MESSAGE")
        fun text(key: String): String = values[key] as? String ?: fail("INVALID_MESSAGE")
        if (type == "HELLO") {
            if (text("version") != "0.1") fail("UNSUPPORTED_VERSION")
            if (text("service") != "file.p0") fail("UNSUPPORTED_SERVICE")
            if (text("role") !in setOf("sender", "receiver") || !fingerprint.matches(text("identity"))) fail("INVALID_MESSAGE")
        } else {
            if (!(type == "ERROR" && values["transferId"] == null) && !transferId.matches(text("transferId"))) fail("INVALID_MESSAGE")
            if (type == "REJECT" && text("reason") !in setOf("USER_DECLINED", "NO_SPACE", "BUSY", "POLICY")) fail("INVALID_MESSAGE")
            if (type == "ERROR" && text("code") !in errors) fail("INVALID_MESSAGE")
            if (type == "OFFER" || type == "COMPLETE") {
                if (values["sizeBytes"] !is Long) fail("INVALID_MESSAGE")
                if (values["sizeBytes"] != SIZE) fail(if (type == "COMPLETE") "SIZE_MISMATCH" else "INVALID_MESSAGE")
                if (text("sha256") != HASH) fail(if (type == "COMPLETE") "HASH_MISMATCH" else "INVALID_MESSAGE")
                if (type == "OFFER" && text("fixture") != FIXTURE) fail("INVALID_MESSAGE")
            }
        }
        return Control(values)
    }

    /** Debug private-file command format also permits explicit boolean consent, never protocol controls. */
    fun parseFlatObject(bytes: ByteArray, maximumBytes: Int, allowBooleans: Boolean = false): Map<String, Any?> {
        if (bytes.isEmpty() || bytes.size > maximumBytes) fail("INVALID_MESSAGE")
        val source = try { Charsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT)
            .onUnmappableCharacter(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(bytes)).toString() }
        catch (_: Exception) { fail("INVALID_MESSAGE") }
        return Parser(source, allowBooleans).parse()
    }
    fun json(type: String, vararg fields: Pair<String, Any?>): ByteArray {
        val values = linkedMapOf<String, Any?>("type" to type).apply { putAll(fields) }
        val source = values.entries.joinToString(",", "{", "}") { (key, value) ->
            "\"$key\":" + when (value) { null -> "null"; is Long, is Int -> value.toString(); is String -> "\"${value.replace("\\", "\\\\").replace("\"", "\\\"")}\""; else -> fail("INVALID_MESSAGE") }
        }
        return source.toByteArray(Charsets.UTF_8).also { parse(it) }
    }
    fun read(input: InputStream, remaining: Long = SIZE, frameStarted: (Int) -> Unit = {},
        clock: () -> Long = { System.nanoTime() / 1_000_000 }, beforeRead: (Long) -> Unit = {}): Frame {
        // The callback applies a deadline BEFORE every blocking read, including partial headers.
        beforeRead(Long.MAX_VALUE) // Waiting for the FIRST byte uses the current phase's budget.
        val kind = input.read()
        if (kind < 0) throw EOFException("closed_before_frame")
        val headerDeadline = clock() + 10_000
        frameStarted(kind)
        val header = exact(input, 4, headerDeadline, clock, beforeRead)
        val length = header.fold(0L) { result, byte -> (result shl 8) or (byte.toInt() and 255).toLong() }
        val maximum = when (kind) { 1 -> 4096L; 2 -> 65536L; else -> fail("INVALID_FRAME") }
        if (length == 0L || length > maximum) fail("INVALID_FRAME")
        if (kind == 2 && length > remaining) fail("SIZE_MISMATCH")
        return Frame(kind, exact(input, length.toInt(), if (kind == 2) clock() + 30_000 else headerDeadline, clock, beforeRead))
    }
    private fun exact(input: InputStream, length: Int, deadline: Long, clock: () -> Long, beforeRead: (Long) -> Unit): ByteArray {
        val bytes = ByteArray(length); var count = 0
        while (count < length) {
            val budget = deadline - clock()
            if (budget <= 0) fail("TIMEOUT")
            beforeRead(budget)
            val read = input.read(bytes, count, length - count)
            if (read < 0) throw EOFException("truncated_frame")
            if (read == 0) continue
            count += read
        }
        return bytes
    }
    fun write(output: OutputStream, kind: Int, bytes: ByteArray) {
        val maximum = when (kind) { 1 -> 4096; 2 -> 65536; else -> fail("INVALID_FRAME") }
        if (bytes.isEmpty() || bytes.size > maximum) fail("INVALID_FRAME")
        output.write(byteArrayOf(kind.toByte(), (bytes.size ushr 24).toByte(), (bytes.size ushr 16).toByte(), (bytes.size ushr 8).toByte(), bytes.size.toByte()))
        output.write(bytes); output.flush()
    }
    fun expect(frame: Frame, type: String, id: String? = null): Control {
        if (frame.kind != 1) fail("UNEXPECTED_MESSAGE")
        val control = parse(frame.payload)
        if (control.type == "ERROR") {
            if (control.values["transferId"] != id) fail("UNEXPECTED_MESSAGE")
            throw PeerFailure(control.string("code"))
        }
        if (control.type != type || (id != null && control.values["transferId"] != id)) fail("UNEXPECTED_MESSAGE")
        return control
    }
    fun fail(code: String): Nothing = throw Failure(code)

    private class Parser(private val source: String, private val allowBooleans: Boolean = false) {
        private var offset = 0
        fun parse(): Map<String, Any?> {
            ws(); take('{'); ws(); val result = linkedMapOf<String, Any?>()
            if (peek() != '}') while (true) {
                val key = string(); ws(); take(':'); ws()
                if (result.containsKey(key)) fail("INVALID_MESSAGE")
                result[key] = when (peek()) {
                    '"' -> string()
                    'n' -> { if (!source.startsWith("null", offset)) fail("INVALID_MESSAGE"); offset += 4; null }
                    't' -> { if (!allowBooleans || !source.startsWith("true", offset)) fail("INVALID_MESSAGE"); offset += 4; true }
                    'f' -> { if (!allowBooleans || !source.startsWith("false", offset)) fail("INVALID_MESSAGE"); offset += 5; false }
                    in '0'..'9' -> number()
                    else -> fail("INVALID_MESSAGE")
                }
                ws(); if (peek() != ',') break; offset++; ws()
            }
            take('}'); ws(); if (offset != source.length) fail("INVALID_MESSAGE")
            return result
        }
        private fun number(): Long {
            val start = offset
            if (peek() == '0') offset++ else while (peek() in '0'..'9') offset++
            val token = source.substring(start, offset)
            val number = token.toLongOrNull() ?: fail("INVALID_MESSAGE")
            if (number > 4294967295L) fail("INVALID_MESSAGE")
            return number
        }
        private fun string(): String {
            take('"'); val value = StringBuilder()
            while (true) {
                val char = peek(); if (char == '\u0000') fail("INVALID_MESSAGE"); offset++
                if (char == '"') break
                if (char < ' ') fail("INVALID_MESSAGE")
                if (char != '\\') value.append(char) else {
                    val escaped = peek(); offset++
                    value.append(when (escaped) { '"', '\\', '/' -> escaped; 'b' -> '\b'; 'f' -> '\u000c'; 'n' -> '\n'; 'r' -> '\r'; 't' -> '\t'; 'u' -> {
                        if (offset + 4 > source.length) fail("INVALID_MESSAGE")
                        val hex = source.substring(offset, offset + 4)
                        if (!hex.all { it in '0'..'9' || it in 'a'..'f' || it in 'A'..'F' }) fail("INVALID_MESSAGE")
                        offset += 4; hex.toInt(16).toChar()
                    }; else -> fail("INVALID_MESSAGE") })
                }
            }
            val text = value.toString(); var position = 0
            while (position < text.length) {
                val char = text[position++]
                if (char.isHighSurrogate()) { if (position == text.length || !text[position++].isLowSurrogate()) fail("INVALID_MESSAGE") }
                else if (char.isLowSurrogate()) fail("INVALID_MESSAGE")
            }
            return text
        }
        private fun peek(): Char = source.getOrNull(offset) ?: '\u0000'
        private fun ws() { while (peek() in setOf(' ', '\n', '\r', '\t')) offset++ }
        private fun take(char: Char) { if (peek() != char) fail("INVALID_MESSAGE"); offset++ }
    }
}
