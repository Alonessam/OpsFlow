using OpsFlow.Core.Abstractions;

namespace OpsFlow.Infrastructure.Security;

/// <summary>
/// BCrypt with the "enhanced" mode: the password is SHA-384 pre-hashed, so bcrypt's silent 72-byte
/// truncation cannot make two long passwords equivalent.
/// </summary>
public sealed class BCryptPasswordHasher(int workFactor = 12) : IPasswordHasher
{
    private readonly Lazy<string> _dummyHash = new(() =>
        BCrypt.Net.BCrypt.EnhancedHashPassword(Guid.NewGuid().ToString("N"), workFactor));

    public string Hash(string password) => BCrypt.Net.BCrypt.EnhancedHashPassword(password, workFactor);

    public bool Verify(string password, string hash)
    {
        try
        {
            return BCrypt.Net.BCrypt.EnhancedVerify(password, hash);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            return false;
        }
    }

    public void VerifyAgainstDummy(string password) => BCrypt.Net.BCrypt.EnhancedVerify(password, _dummyHash.Value);
}
