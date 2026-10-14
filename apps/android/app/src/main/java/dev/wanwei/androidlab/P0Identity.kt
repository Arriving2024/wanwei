package dev.wanwei.androidlab

import android.os.Build
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.math.BigInteger
import java.net.Socket
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.MessageDigest
import java.security.Principal
import java.security.PrivateKey
import java.security.SecureRandom
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.security.interfaces.ECPublicKey
import java.security.interfaces.RSAPublicKey
import java.security.spec.ECGenParameterSpec
import java.util.Date
import javax.net.ssl.SSLContext
import javax.net.ssl.X509ExtendedKeyManager
import javax.net.ssl.X509TrustManager
import javax.security.auth.x500.X500Principal

/** Install-scoped non-exportable AndroidKeyStore identity. Public certificate alone is exported. */
internal class P0Identity {
    private val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
    init {
        if (!store.containsAlias(ALIAS)) {
            val generator = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, "AndroidKeyStore")
            generator.initialize(KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_SIGN or KeyProperties.PURPOSE_VERIFY)
                .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
                .setDigests(KeyProperties.DIGEST_NONE, KeyProperties.DIGEST_SHA256)
                .setCertificateSubject(X500Principal("CN=Wanwei P0 install identity"))
                .setCertificateSerialNumber(BigInteger(159, SecureRandom()).add(BigInteger.ONE))
                .setCertificateNotBefore(Date(System.currentTimeMillis() - 86_400_000L))
                .setCertificateNotAfter(Date(System.currentTimeMillis() + 5L * 365 * 86_400_000L))
                .build())
            generator.generateKeyPair()
        }
    }
    val certificate: X509Certificate get() = store.getCertificate(ALIAS) as X509Certificate
    val fingerprint: String get() = fingerprint(certificate)

    /** A fresh context is used once; no persisted/cache reuse and no pre-handshake application data. */
    fun newContext(peerPin: String): SSLContext {
        if (Build.VERSION.SDK_INT < 29 || !P0Protocol.fingerprint.matches(peerPin)) P0Protocol.fail("IDENTITY_MISMATCH")
        val cert = certificate
        validateCertificate(cert, fingerprint, "server")
        val privateKey = store.getKey(ALIAS, null) as PrivateKey
        val manager = object : X509ExtendedKeyManager() {
            override fun getClientAliases(keyType: String?, issuers: Array<out Principal>?): Array<String>? = if (keyType?.startsWith("EC") == true) arrayOf(ALIAS) else null
            override fun chooseClientAlias(keyType: Array<out String>?, issuers: Array<out Principal>?, socket: Socket?): String? = if (keyType?.any { it.startsWith("EC") } == true) ALIAS else null
            override fun getServerAliases(keyType: String?, issuers: Array<out Principal>?): Array<String>? = getClientAliases(keyType, issuers)
            override fun chooseServerAlias(keyType: String?, issuers: Array<out Principal>?, socket: Socket?): String? = if (keyType?.startsWith("EC") == true) ALIAS else null
            override fun getCertificateChain(alias: String?): Array<X509Certificate>? = if (alias == ALIAS) arrayOf(cert) else null
            override fun getPrivateKey(alias: String?): PrivateKey? = if (alias == ALIAS) privateKey else null
        }
        val trust = object : X509TrustManager {
            override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
            override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) = check(chain, "client")
            override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) = check(chain, "server")
            private fun check(chain: Array<out X509Certificate>?, role: String) {
                if (chain == null || chain.size != 1) throw CertificateException("P0 requires one pinned self-signed install certificate")
                validateCertificate(chain.single(), peerPin, role)
            }
        }
        return SSLContext.getInstance("TLSv1.3").apply { init(arrayOf(manager), arrayOf(trust), SecureRandom()) }
    }
    companion object {
        private const val ALIAS = "wanwei-p0-install-ec-v1"
        fun fingerprint(cert: X509Certificate): String = MessageDigest.getInstance("SHA-256").digest(cert.publicKey.encoded).joinToString("") { "%02x".format(it.toInt() and 255) }
        fun validateCertificate(cert: X509Certificate, pin: String, role: String) {
            cert.checkValidity()
            if (cert.basicConstraints != -1 || cert.subjectX500Principal != cert.issuerX500Principal) throw CertificateException("P0 leaf self-signed certificate required")
            if (cert.sigAlgOID !in setOf("1.2.840.113549.1.1.11", "1.2.840.113549.1.1.12", "1.2.840.113549.1.1.13", "1.2.840.10045.4.3.2", "1.2.840.10045.4.3.3", "1.2.840.10045.4.3.4")) throw CertificateException("P0 certificate signature policy")
            val key = cert.publicKey
            if (!((key is RSAPublicKey && key.modulus.bitLength() >= 2048) || (key is ECPublicKey && key.params.curve.field.fieldSize >= 256))) throw CertificateException("P0 key strength policy")
            val usage = cert.keyUsage
            if (usage != null && (usage.isEmpty() || !usage[0])) throw CertificateException("digitalSignature required")
            // AndroidKeyStore's install self-signed certificate may omit KU/EKU. If present, constrain them.
            val extended = cert.extendedKeyUsage
            val roleOid = if (role == "server") "1.3.6.1.5.5.7.3.1" else "1.3.6.1.5.5.7.3.2"
            if (extended != null && roleOid !in extended) throw CertificateException("certificate role mismatch")
            val knownCritical = setOf("2.5.29.15", "2.5.29.19", "2.5.29.37")
            if (cert.criticalExtensionOIDs?.any { it !in knownCritical } == true) throw CertificateException("unknown critical certificate extension")
            cert.verify(key)
            val actual = fingerprint(cert)
            if (!P0Protocol.fingerprint.matches(pin) || !MessageDigest.isEqual(actual.toByteArray(Charsets.US_ASCII), pin.toByteArray(Charsets.US_ASCII))) throw CertificateException("P0 public-key pin mismatch")
        }
    }
}
