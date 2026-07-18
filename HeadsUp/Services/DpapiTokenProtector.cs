using System.Security.Cryptography;

namespace HeadsUp.Services;

/// <summary>
/// Encrypts stored tokens with Windows DPAPI, scoped to the current user. The OS holds the
/// key material, so there's nothing for Heads-Up to store or for another user on the machine
/// to read. The flip side is that the file is bound to this user on this machine — copying it
/// elsewhere yields an undecryptable blob, which
/// <see cref="FileSlackTokenStore"/> treats as "not signed in".
/// </summary>
public sealed class DpapiTokenProtector : ITokenProtector
{
    // Ties the ciphertext to this app's use of DPAPI, so a blob from elsewhere in the user's
    // profile can't be swapped in and decrypted as if it were ours.
    private static readonly byte[] Entropy = "HeadsUp.SlackAppTokens.v1"u8.ToArray();

    public byte[] Protect(byte[] plaintext) =>
        ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] ciphertext) =>
        ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
}
