using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace RTFM.Licensing;

/// <summary>The installation's own P-256 key. It identifies this copy of the app to the server; no hardware
/// identifiers are collected. On Windows it is created in the TPM when one is available, so it can't be copied to
/// another PC; otherwise it is a software key kept in the (encrypted) state.</summary>
internal sealed class InstallationIdentity : IDisposable
{
    internal const string TpmProvider = "Microsoft Platform Crypto Provider";
    private readonly ECDsa key;

    public InstallationKeyReference Reference { get; }
    public PublicJwk PublicKey { get; }
    public string Thumbprint => PublicKey.Thumbprint;

    private InstallationIdentity(ECDsa key, InstallationKeyReference reference)
    {
        this.key = key;
        Reference = reference;
        PublicKey = PublicJwk.FromParameters(key.ExportParameters(false));
    }

    /// <summary>A new key: in the TPM when <paramref name="preferTpm"/> and Windows can make one there, otherwise in
    /// software.</summary>
    public static InstallationIdentity Generate(bool preferTpm = true)
    {
        if (preferTpm && OperatingSystem.IsWindows() && TryCreateTpmKey() is { } tpm) return tpm;
        var software = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new InstallationIdentity(software, new InstallationKeyReference("software", Pkcs8: software.ExportPkcs8PrivateKey()));
    }

    /// <summary>The key a stored reference names.</summary>
    public static InstallationIdentity Open(InstallationKeyReference reference)
    {
        switch (reference.Storage)
        {
            case "software" when reference.Pkcs8 is { } pkcs8:
                var software = ECDsa.Create();
                software.ImportPkcs8PrivateKey(pkcs8, out _);
                if (software.KeySize != 256) throw new CryptographicException("The installation key is not P-256.");
                return new InstallationIdentity(software, reference);
            case "tpm" when reference.KeyName is { } name && OperatingSystem.IsWindows():
                return OpenTpmKey(name, reference);
            default:
                throw new CryptographicException("The installation key is not available on this computer.");
        }
    }

    /// <summary>Signs a challenge proof message and returns the raw r||s signature, base64url.</summary>
    public string Sign(string message) =>
        Base64Url.Encode(key.SignData(Encoding.UTF8.GetBytes(message), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    /// <summary>Deletes a TPM key; a software key goes with the state.</summary>
    public static void Delete(InstallationKeyReference reference)
    {
        if (reference is { Storage: "tpm", KeyName: { } name } && OperatingSystem.IsWindows()) DeleteTpmKey(name);
    }

    public void Dispose() => key.Dispose();

    [SupportedOSPlatform("windows")]
    private static InstallationIdentity? TryCreateTpmKey()
    {
        var name = "RTFM.Licensing." + Guid.NewGuid().ToString("N");
        try
        {
            var parameters = new CngKeyCreationParameters
            {
                Provider = new CngProvider(TpmProvider),
                KeyUsage = CngKeyUsages.Signing,
                ExportPolicy = CngExportPolicies.None,
            };
            var cng = CngKey.Create(CngAlgorithm.ECDsaP256, name, parameters);
            return new InstallationIdentity(new ECDsaCng(cng), new InstallationKeyReference("tpm", KeyName: name));
        }
        catch (CryptographicException)
        {
            // No TPM, a TPM that refuses new keys, or a policy that blocks it: a software key is used instead.
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static InstallationIdentity OpenTpmKey(string name, InstallationKeyReference reference) =>
        new(new ECDsaCng(CngKey.Open(name, new CngProvider(TpmProvider))), reference);

    [SupportedOSPlatform("windows")]
    private static void DeleteTpmKey(string name)
    {
        try { using var key = CngKey.Open(name, new CngProvider(TpmProvider)); key.Delete(); }
        catch (CryptographicException) { }
    }
}
