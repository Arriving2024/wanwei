package dev.wanwei.androidlab

/** Private-directory/single-writer policy. This is not a CAS against a malicious same-UID process. */
internal object P0OwnedCommit {
    data class Identity(val key: String, val owner: Int, val regular: Boolean, val directory: Boolean,
        val permissions: Int, val links: Long, val size: Long)
    fun ownsFile(expected: Identity?, observed: Identity?): Boolean = expected != null && observed != null &&
        expected.regular && observed.regular && expected.key == observed.key && expected.owner == observed.owner &&
        expected.links == 1L && observed.links == 1L
    fun ownsDirectory(expected: Identity, observed: Identity): Boolean = expected.directory && observed.directory &&
        expected.key == observed.key && expected.owner == observed.owner && observed.permissions == 448
    fun commit(directory: String, partial: String, reservation: String, directoryIdentity: Identity,
        partialIdentity: Identity, reservationIdentity: Identity, observe: (String) -> Identity,
        rename: (String, String) -> Unit) {
        val currentDirectory = observe(directory)
        val currentPartial = observe(partial)
        val currentReservation = observe(reservation)
        if (!ownsDirectory(directoryIdentity, currentDirectory) || !ownsFile(partialIdentity, currentPartial) ||
            currentPartial.size != P0Protocol.SIZE || !ownsFile(reservationIdentity, currentReservation) ||
            currentReservation.size != 0L || partialIdentity.key == reservationIdentity.key) P0Protocol.fail("STORAGE_FAILED")
        // The only replaced name was exclusively created by this task. Existing/colliding names
        // never become reservations. The app-private 0700 directory has a single writer.
        rename(partial, reservation)
    }
}
