package dev.wanwei.androidlab

import org.junit.Assert.*
import org.junit.Test
import java.nio.file.Files
import java.nio.file.LinkOption
import java.nio.file.Path
import java.nio.file.StandardCopyOption
import java.nio.file.StandardOpenOption
import java.nio.file.attribute.BasicFileAttributes

class P0OwnedCommitTest {
    private val identityWitnesses = mutableListOf<Pair<String, Path>>()
    private fun identity(path: String): P0OwnedCommit.Identity {
        val source = Path.of(path)
        val attrs = Files.readAttributes(source, BasicFileAttributes::class.java, LinkOption.NOFOLLOW_LINKS)
        val key = attrs.fileKey()?.toString() ?: if (attrs.isDirectory) "host-directory:${source.toRealPath()}"
            else identityWitnesses.firstOrNull { Files.isSameFile(source, it.second) }?.first ?: run {
                // Windows BasicFileAttributes may omit fileKey. This HOST-TEST adapter keeps an
                // inode witness so replacements still use real filesystem identity (not timestamps).
                // Android production uses Os.fstat dev:ino and does not use hardlinks.
                val token = java.util.UUID.randomUUID().toString()
                val witness = source.parent.resolve("identity-witness-$token")
                Files.createLink(witness, source)
                identityWitnesses.add(token to witness)
                token
            }
        return P0OwnedCommit.Identity(key, 1, attrs.isRegularFile, attrs.isDirectory, 448, 1, attrs.size())
    }
    private fun fixture(test: (Path, Path, Path) -> Unit) {
        val directory = Files.createTempDirectory("wanwei-commit-test-")
        try {
            val partial = directory.resolve("owned.partial")
            Files.write(partial, ByteArray(P0Protocol.SIZE.toInt()) { it.toByte() }, StandardOpenOption.CREATE_NEW)
            val reserved = Files.createFile(directory.resolve("owned.bin"))
            test(directory, partial, reserved)
        } finally {
            Files.list(directory).use { children -> children.forEach { Files.deleteIfExists(it) } }
            Files.deleteIfExists(directory)
        }
    }
    private fun failure(action: () -> Unit) {
        try { action(); fail("Commit must reject changed ownership") }
        catch (failure: P0Protocol.Failure) { assertEquals("STORAGE_FAILED", failure.code) }
    }
    private fun commit(directory: Path, partial: Path, reserved: Path, directoryIdentity: P0OwnedCommit.Identity,
        partialIdentity: P0OwnedCommit.Identity, reservationIdentity: P0OwnedCommit.Identity) {
        P0OwnedCommit.commit(directory.toString(), partial.toString(), reserved.toString(), directoryIdentity,
            partialIdentity, reservationIdentity, ::identity) { from, to ->
            Files.move(Path.of(from), Path.of(to), StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING)
        }
    }
    @Test fun atomicCommitOnlyReplacesItsOwnExclusiveEmptyReservation() = fixture { directory, partial, reserved ->
        val unrelated = Files.write(directory.resolve("existing-user-file.bin"), byteArrayOf(9, 8, 7), StandardOpenOption.CREATE_NEW)
        commit(directory, partial, reserved, identity(directory.toString()), identity(partial.toString()), identity(reserved.toString()))
        assertFalse(Files.exists(partial))
        assertEquals(P0Protocol.SIZE, Files.size(reserved))
        assertArrayEquals(byteArrayOf(9, 8, 7), Files.readAllBytes(unrelated))
        val hash = java.security.MessageDigest.getInstance("SHA-256").digest(Files.readAllBytes(reserved)).joinToString("") { "%02x".format(it.toInt() and 255) }
        assertEquals(P0Protocol.HASH, hash)
    }
    @Test fun exclusiveReservationCollisionLeavesExistingFileUntouched() = fixture { _, _, reserved ->
        Files.write(reserved, byteArrayOf(1, 2, 3), StandardOpenOption.TRUNCATE_EXISTING)
        try { Files.newByteChannel(reserved, StandardOpenOption.CREATE_NEW, StandardOpenOption.WRITE).close(); fail("Must not reserve existing target") }
        catch (_: java.nio.file.FileAlreadyExistsException) { }
        assertArrayEquals(byteArrayOf(1, 2, 3), Files.readAllBytes(reserved))
    }
    @Test fun replacingReservationWithForeignInodeCannotCommitOrCleanupIt() = fixture { directory, partial, reserved ->
        val directoryIdentity = identity(directory.toString()); val partialIdentity = identity(partial.toString())
        val oldReservation = identity(reserved.toString())
        val foreign = Files.write(directory.resolve("foreign.bin"), byteArrayOf(6, 5, 4), StandardOpenOption.CREATE_NEW)
        Files.move(foreign, reserved, StandardCopyOption.REPLACE_EXISTING)
        failure { commit(directory, partial, reserved, directoryIdentity, partialIdentity, oldReservation) }
        if (P0OwnedCommit.ownsFile(oldReservation, identity(reserved.toString()))) Files.delete(reserved)
        assertArrayEquals(byteArrayOf(6, 5, 4), Files.readAllBytes(reserved))
        assertTrue(Files.exists(partial))
    }
    @Test fun nonemptyReservationIsNotAnAuthorizedCommitTarget() = fixture { directory, partial, reserved ->
        val directoryIdentity = identity(directory.toString()); val partialIdentity = identity(partial.toString())
        val reservationIdentity = identity(reserved.toString())
        Files.write(reserved, byteArrayOf(7), StandardOpenOption.TRUNCATE_EXISTING)
        failure { commit(directory, partial, reserved, directoryIdentity, partialIdentity, reservationIdentity) }
        assertArrayEquals(byteArrayOf(7), Files.readAllBytes(reserved))
    }
    @Test fun truncatedOrSubstitutedPartialCannotBeCommitted() = fixture { directory, partial, reserved ->
        val directoryIdentity = identity(directory.toString()); val partialIdentity = identity(partial.toString())
        val reservationIdentity = identity(reserved.toString())
        Files.write(partial, byteArrayOf(1), StandardOpenOption.TRUNCATE_EXISTING)
        failure { commit(directory, partial, reserved, directoryIdentity, partialIdentity, reservationIdentity) }
        val other = Files.write(directory.resolve("other.partial"), ByteArray(P0Protocol.SIZE.toInt()), StandardOpenOption.CREATE_NEW)
        Files.move(other, partial, StandardCopyOption.REPLACE_EXISTING)
        failure { commit(directory, partial, reserved, directoryIdentity, partialIdentity, reservationIdentity) }
        assertEquals(0L, Files.size(reserved))
    }
    @Test fun cleanupOwnershipRejectsNonregularForeignOwnerAndDifferentDeviceIdentity() = fixture { _, partial, reserved ->
        val owned = identity(partial.toString())
        assertFalse(P0OwnedCommit.ownsFile(owned, owned.copy(regular = false)))
        assertFalse(P0OwnedCommit.ownsFile(owned, owned.copy(key = "other-device:${owned.key}")))
        assertFalse(P0OwnedCommit.ownsFile(owned, owned.copy(owner = 2)))
        assertFalse(P0OwnedCommit.ownsFile(owned, owned.copy(links = 2)))
        assertFalse(P0OwnedCommit.ownsFile(owned, identity(reserved.toString())))
    }
}
