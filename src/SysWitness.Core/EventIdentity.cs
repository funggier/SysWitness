using System.Security.Cryptography;
using System.Text;

namespace SysWitness.Core;

/// <summary>
/// Stable content-derived ID for source records. Correlation and risk are not encoded in identities.
/// </summary>
public static class EventIdentity
{
    public static string FromXml(string channel, string xml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(channel + "\n" + xml))).ToLowerInvariant();
    }
}
