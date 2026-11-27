using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace TeamBar;

internal interface IReleaseUpdateVerifier
{
    void VerifyManifest(byte[] manifest, byte[] signature, string certificateSha256);
    void VerifyInstaller(string path, string certificateSha256);
}

internal sealed class ReleaseUpdateTrust : IReleaseUpdateVerifier
{
    public void VerifyManifest(byte[] manifest, byte[] signature, string certificateSha256)
    {
        if (signature.Length == 0 || signature.Length > ReleaseUpdateManifest.SignatureLimit)
            throw new InvalidDataException("The update signature is empty or too large.");
        var cms = new SignedCms(new ContentInfo(manifest), detached: true);
        cms.Decode(signature);
        if (!cms.Detached || !cms.ContentInfo.Content.AsSpan().SequenceEqual(manifest))
            throw new CryptographicException("The release signature must cover the exact detached manifest bytes.");
        if (cms.SignerInfos.Count != 1 || cms.SignerInfos[0].Certificate is not { } certificate)
            throw new CryptographicException("The update manifest must have exactly one identified publisher.");
        VerifyPin(certificate, certificateSha256);
        if (cms.SignerInfos[0].DigestAlgorithm.Value is not ("2.16.840.1.101.3.4.2.1" or "2.16.840.1.101.3.4.2.2" or "2.16.840.1.101.3.4.2.3"))
            throw new CryptographicException("The update manifest uses an unsupported signature digest.");
        cms.CheckSignature(verifySignatureOnly: true);
        // The dedicated metadata signer is an immutable trust anchor, independent of short-lived
        // Authenticode leaves. It may be private/self-signed; OS trust is required for the installer below.
        var now = DateTime.UtcNow;
        if (now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime()
            || certificate.Extensions.OfType<X509KeyUsageExtension>().Any(usage => !usage.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature)))
            throw new CryptographicException("The pinned release-signing certificate is expired, not yet valid, or not permitted to sign.");
    }

    public void VerifyInstaller(string path, string certificateSha256)
    {
        using var file = new TrustFile(path);
        var trust = new TrustData(file.Pointer);
        Guid action = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        try
        {
            int result = WinVerifyTrust(new nint(-1), ref action, ref trust);
            if (result != 0) throw new CryptographicException($"Windows rejected the update installer's Authenticode signature (0x{result:X8}).");
            // Pin the actual signer Windows just verified, including when the PE has multiple signatures.
            nint provider = WTHelperProvDataFromStateData(trust.StateData);
            nint signer = provider == 0 ? 0 : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            nint trustedCertificate = signer == 0 ? 0 : WTHelperGetProvCertFromChain(signer, 0);
            if (trustedCertificate == 0) throw new CryptographicException("Windows did not return the verified installer's signer.");
            var providerCertificate = Marshal.PtrToStructure<ProviderCertificate>(trustedCertificate);
            if (providerCertificate.Certificate == 0) throw new CryptographicException("The verified installer has no publisher certificate.");
            var context = Marshal.PtrToStructure<CertificateContext>(providerCertificate.Certificate);
            if (context.Encoded == 0 || context.EncodedLength is 0 or > 65536) throw new CryptographicException("The verified publisher certificate is invalid.");
            byte[] encoded = new byte[context.EncodedLength];
            Marshal.Copy(context.Encoded, encoded, 0, encoded.Length);
            using var certificate = X509CertificateLoader.LoadCertificate(encoded);
            VerifyPin(certificate, certificateSha256);
        }
        finally
        {
            trust.StateAction = 2; // WTD_STATEACTION_CLOSE, including failure paths.
            _ = WinVerifyTrust(new nint(-1), ref action, ref trust);
        }
    }

    private static void VerifyPin(X509Certificate2 certificate, string expected)
    {
        if (!ReleaseUpdateManifest.IsHash(expected) || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.RawData), Convert.FromHexString(expected)))
            throw new CryptographicException("The update was not signed by this build's trusted publisher.");
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(nint window, ref Guid action, ref TrustData data);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperProvDataFromStateData(nint stateData);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperGetProvSignerFromChain(nint provider, uint signerIndex, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperGetProvCertFromChain(nint signer, uint certificateIndex);

    // Prefixes of CRYPT_PROVIDER_CERT and CERT_CONTEXT; only these fields are inspected.
    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderCertificate { internal uint Size; internal nint Certificate; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CertificateContext { internal uint Encoding; internal nint Encoded; internal uint EncodedLength; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustFileInfo
    {
        internal uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] internal string Path;
        internal nint FileHandle, KnownSubject;
    }
    private sealed class TrustFile : IDisposable
    {
        internal nint Pointer { get; }
        internal TrustFile(string path)
        {
            Pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFileInfo>());
            Marshal.StructureToPtr(new TrustFileInfo { Size = (uint)Marshal.SizeOf<TrustFileInfo>(), Path = path }, Pointer, false);
        }
        public void Dispose() { Marshal.DestroyStructure<TrustFileInfo>(Pointer); Marshal.FreeHGlobal(Pointer); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        internal uint Size;
        internal nint PolicyCallbackData, SipClientData;
        internal uint UiChoice, RevocationChecks, UnionChoice;
        internal nint FileInfo;
        internal uint StateAction;
        internal nint StateData, UrlReference;
        internal uint ProviderFlags, UiContext;
        internal nint SignatureSettings;
        internal TrustData(nint file)
        {
            this = default;
            Size = (uint)Marshal.SizeOf<TrustData>();
            UiChoice = 2; // WTD_UI_NONE
            RevocationChecks = 1; // WTD_REVOKE_WHOLECHAIN
            UnionChoice = 1; // WTD_CHOICE_FILE
            FileInfo = file;
            StateAction = 1; // WTD_STATEACTION_VERIFY, retain the verified signer until inspected.
            ProviderFlags = 0x80 | 0x2000; // Revocation for chain excluding root, disable MD2/MD4.
        }
    }
}
