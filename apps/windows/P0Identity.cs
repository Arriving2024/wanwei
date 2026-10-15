using System.Formats.Asn1;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Wanwei;

internal sealed class P0Identity : IDisposable
{
    internal X509Certificate2 Certificate { get; }
    internal string Fingerprint { get; }
    private P0Identity(X509Certificate2 certificate) { Certificate = certificate; Fingerprint = FingerprintOf(certificate); }
    internal static P0Identity ForHostTest(X509Certificate2 certificate) => new(certificate);
    internal static P0Identity Load()
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WanweiLab", "Identity");
        P0Storage.EnsureSafeDirectory(directory);
        string path = Path.Combine(directory, "identity-v1.dpapi");
        if (File.Exists(path))
        {
            P0Storage.RejectReparse(path);
            byte[] pfx = Dpapi.Transform(File.ReadAllBytes(path), false);
            try { return new(X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet)); }
            finally { CryptographicOperations.ZeroMemory(pfx); }
        }
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Wanwei P0 Installation", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection {
            new("1.3.6.1.5.5.7.3.1"), new("1.3.6.1.5.5.7.3.2") }, true));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        byte[] plain = generated.Export(X509ContentType.Pfx);
        try
        {
            byte[] protectedBytes = Dpapi.Transform(plain, true);
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write(protectedBytes); file.Flush(true);
            // Schannel needs an OS key handle that it can acquire by name. A
            // transient CurrentUser key container is platform ACL protected and
            // is deleted on certificate disposal; the retained PFX is DPAPI only.
            return new(X509CertificateLoader.LoadPkcs12(plain, null, X509KeyStorageFlags.UserKeySet));
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    internal static string FingerprintOf(X509Certificate2 cert) => Convert.ToHexStringLower(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()));
    internal void ExportPublic(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "p0-public-identity.json"), JsonSerializer.Serialize(new {
            schema = "wanwei.p0.public-identity/1", appVersion = "0.3.2", identity = Fingerprint,
            certificateDerBase64 = Convert.ToBase64String(Certificate.RawData), privateKeyExported = false,
            storageProtection = "Windows DPAPI CurrentUser", trust = "peer_full_fingerprint_requires_explicit_local_confirmation"
        }, ReportStore.Json), new UTF8Encoding(false));
    }
    internal static bool ValidatePeer(X509Certificate? rawCertificate, string expectedPin)
    {
        if (rawCertificate is null || !P0Protocol.IsFingerprint(expectedPin)) return false;
        try
        {
            using var cert = new X509Certificate2(rawCertificate);
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(FingerprintOf(cert)), Encoding.ASCII.GetBytes(expectedPin))) return false;
            var now = DateTime.UtcNow;
            if (now < cert.NotBefore.ToUniversalTime() || now > cert.NotAfter.ToUniversalTime()) return false;
            using var rsa = cert.GetRSAPublicKey(); using var ec = cert.GetECDsaPublicKey();
            if ((rsa is null || rsa.KeySize < 2048) && (ec is null || ec.KeySize < 256)) return false;
            if (!cert.SubjectName.RawData.AsSpan().SequenceEqual(cert.IssuerName.RawData)) return false;
            foreach (var extension in cert.Extensions)
            {
                if (extension is X509BasicConstraintsExtension bc && bc.CertificateAuthority) return false;
                if (extension is X509KeyUsageExtension ku && (ku.KeyUsages & X509KeyUsageFlags.DigitalSignature) == 0) return false;
                if (extension is X509EnhancedKeyUsageExtension eku &&
                    (!eku.EnhancedKeyUsages.Cast<Oid>().Any(x => x.Value == "1.3.6.1.5.5.7.3.1") ||
                    !eku.EnhancedKeyUsages.Cast<Oid>().Any(x => x.Value == "1.3.6.1.5.5.7.3.2"))) return false;
            }
            // Custom roots alone would treat the certificate as a trust anchor;
            // explicitly verify its self-signature as well as its pinned SPKI.
            var reader = new AsnReader(cert.RawData, AsnEncodingRules.DER);
            var sequence = reader.ReadSequence(); var tbs = sequence.ReadEncodedValue();
            var algorithm = sequence.ReadSequence(); string oid = algorithm.ReadObjectIdentifier();
            if (algorithm.HasData) algorithm.ReadNull(); algorithm.ThrowIfNotEmpty();
            var signature = sequence.ReadBitString(out int unused); sequence.ThrowIfNotEmpty(); reader.ThrowIfNotEmpty();
            if (unused != 0) return false;
            bool verified = oid switch {
                "1.2.840.113549.1.1.11" when rsa is not null => rsa.VerifyData(tbs.Span, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
                "1.2.840.113549.1.1.12" when rsa is not null => rsa.VerifyData(tbs.Span, signature, HashAlgorithmName.SHA384, RSASignaturePadding.Pkcs1),
                "1.2.840.113549.1.1.13" when rsa is not null => rsa.VerifyData(tbs.Span, signature, HashAlgorithmName.SHA512, RSASignaturePadding.Pkcs1),
                "1.2.840.10045.4.3.2" when ec is not null => ec.VerifyData(tbs.Span, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence),
                "1.2.840.10045.4.3.3" when ec is not null => ec.VerifyData(tbs.Span, signature, HashAlgorithmName.SHA384, DSASignatureFormat.Rfc3279DerSequence),
                "1.2.840.10045.4.3.4" when ec is not null => ec.VerifyData(tbs.Span, signature, HashAlgorithmName.SHA512, DSASignatureFormat.Rfc3279DerSequence),
                _ => false
            };
            if (!verified) return false;
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(cert);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            return chain.Build(cert);
        }
        catch (Exception e) when (e is CryptographicException or AsnContentException or ArgumentException) { return false; }
    }
    public void Dispose() => Certificate.Dispose();

    private static class Dpapi
    {
        [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr value);
        internal static byte[] Transform(byte[] input, bool protect)
        {
            var blob = new Blob { Size = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
            try
            {
                Marshal.Copy(input, 0, blob.Data, input.Length);
                bool ok = protect ? CryptProtectData(ref blob, "Wanwei P0 install identity", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out var output) :
                    CryptUnprotectData(ref blob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
                if (!ok) throw new CryptographicException("IDENTITY_PROTECTION_FAILED");
                try { var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result; }
                finally { LocalFree(output.Data); }
            }
            finally
            {
                byte[] zeros = new byte[input.Length]; Marshal.Copy(zeros, 0, blob.Data, zeros.Length); Marshal.FreeHGlobal(blob.Data);
            }
        }
    }
}
