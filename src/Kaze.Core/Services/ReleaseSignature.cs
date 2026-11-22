using System.Security.Cryptography;

namespace Kaze.Core.Services;

/// <summary>
/// Release signatures: ECDSA P-256 over a small statement that binds the version to the setup
/// program's SHA-256, stored as base64 in a <c>.sig</c> file next to it. build/publish.ps1 signs with
/// the private key, which never leaves the release machine. The app carries only the public key, so
/// a setup that was changed after signing, published by anyone else, or an older signed setup
/// renamed as a newer version, is refused.
/// </summary>
public static class ReleaseSignature
{
    /// <summary>Public key (SubjectPublicKeyInfo, base64) of the official Kaze releases.</summary>
    public const string OfficialPublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEm7draogDWOw9GShaMXvrfD2DLa5OkFs76jOoJXNYinZXoXE5CYsRRTQNirxoItPbGo2XZhD7Cbdb67ukDp3kDA==";

    /// <summary>What gets signed: <c>kaze-release</c>, the version and the setup's SHA-256, one per line.</summary>
    public static byte[] Statement(Stream setup, Version version)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(setup));
        return System.Text.Encoding.UTF8.GetBytes(string.Join('\n', "kaze-release", version.ToString(3), hash, ""));
    }

    public static bool Verify(Stream setup, Version version, string signatureText, string publicKeyBase64 = OfficialPublicKey)
    {
        try
        {
            var signature = Convert.FromBase64String(signatureText.Trim());
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            return key.VerifyData(Statement(setup, version), signature, HashAlgorithmName.SHA256);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public static string Sign(Stream setup, Version version, ECDsa privateKey) =>
        Convert.ToBase64String(privateKey.SignData(Statement(setup, version), HashAlgorithmName.SHA256));
}
