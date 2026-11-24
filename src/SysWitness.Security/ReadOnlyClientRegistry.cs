using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SysWitness.Security;

/// <summary>
/// Local, startup-only read credentials. File-based clients store SHA-256 hashes,
/// not plaintext keys. API consumers cannot create clients or elevate their scopes.
/// </summary>
public sealed class ReadOnlyClientRegistry
{
    public const string EventsRead = "events.read";
    public const string CoverageRead = "coverage.read";
    public const string StorageRead = "storage.read";
    public const string ProcessesRead = "processes.read";
    public const string NetworkRead = "network.read";
    private static readonly HashSet<string> ValidScopes = [EventsRead, CoverageRead, StorageRead, ProcessesRead, NetworkRead];
    private static readonly Regex IdFormat = new(@"\A[a-zA-Z0-9._-]{1,64}\z", RegexOptions.Compiled);
    private readonly IReadOnlyList<ClientEntry> _entries;

    private sealed record ClientEntry(string Id, byte[] Hash, IReadOnlySet<string> Scopes);

    public sealed record ClientIdentity(string Id, IReadOnlySet<string> Scopes)
    {
        public bool CanRead(string scope) => Scopes.Contains(scope);
    }

    private ReadOnlyClientRegistry(IReadOnlyList<ClientEntry> entries) => _entries = entries;

    public static ReadOnlyClientRegistry Load(string? legacyToken, string? clientFile)
    {
        var clients = new List<ClientEntry>();
        if (!string.IsNullOrWhiteSpace(legacyToken))
        {
            if (legacyToken.Length < 32)
                throw new ArgumentException("SYSWITNESS_API_TOKEN must be at least 32 characters.");
            clients.Add(new ClientEntry("local-default",
                SHA256.HashData(Encoding.UTF8.GetBytes(legacyToken)),
                new HashSet<string>([EventsRead, CoverageRead, StorageRead], StringComparer.Ordinal)));
        }

        if (!string.IsNullOrWhiteSpace(clientFile))
        {
            var path = Path.GetFullPath(clientFile);
            if (!File.Exists(path))
                throw new FileNotFoundException("Read-only client registry file does not exist.", path);
            var info = new FileInfo(path);
            if (info.Length is < 1 or > 65_536)
                throw new InvalidDataException("Client registry file must be 1..65536 bytes.");

            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                MaxDepth = 12,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("clients", out var list) ||
                list.ValueKind != JsonValueKind.Array ||
                list.GetArrayLength() is < 1 or > 32)
                throw new InvalidDataException("Client registry must contain 1..32 clients.");
            if (root.EnumerateObject().Any(field => field.Name != "clients"))
                throw new InvalidDataException("Unknown client registry properties are forbidden.");

            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    item.EnumerateObject().Any(field => field.Name is not
                        ("id" or "tokenSha256" or "scopes")))
                    throw new InvalidDataException("Unexpected client property; only hashed keys are accepted.");
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("id", out var idElement) ||
                    !item.TryGetProperty("tokenSha256", out var hashElement) ||
                    !item.TryGetProperty("scopes", out var scopeElement) ||
                    idElement.ValueKind != JsonValueKind.String ||
                    hashElement.ValueKind != JsonValueKind.String ||
                    scopeElement.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Invalid client registry entry.");

                var id = idElement.GetString() ?? "";
                var hashText = hashElement.GetString() ?? "";
                if (!IdFormat.IsMatch(id) || id == "local-default" ||
                    hashText.Length != 64 || !hashText.All(Uri.IsHexDigit))
                    throw new InvalidDataException("Invalid client ID or token SHA-256.");

                var scopes = new HashSet<string>(StringComparer.Ordinal);
                foreach (var scope in scopeElement.EnumerateArray())
                {
                    if (scope.ValueKind != JsonValueKind.String ||
                        scope.GetString() is not { } name || !ValidScopes.Contains(name) ||
                        !scopes.Add(name))
                        throw new InvalidDataException("Unknown or duplicate read scope.");
                }
                if (scopes.Count == 0)
                    throw new InvalidDataException("A client must have at least one read scope.");

                clients.Add(new ClientEntry(id, Convert.FromHexString(hashText), scopes));
            }
        }

        if (clients.Count == 0)
            throw new InvalidOperationException(
                "Set SYSWITNESS_API_TOKEN or SYSWITNESS_CLIENT_KEYS_FILE before starting.");

        if (clients.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != clients.Count)
            throw new InvalidDataException("Duplicate client identifiers.");
        if (clients.Select(x => Convert.ToHexString(x.Hash))
            .Distinct(StringComparer.Ordinal).Count() != clients.Count)
            throw new InvalidDataException("Duplicate API key hashes are forbidden.");

        return new ReadOnlyClientRegistry(clients);
    }

    public bool TryAuthenticate(string? presentedToken, out ClientIdentity? identity)
    {
        identity = null;
        if (presentedToken is null || presentedToken.Length is < 32 or > 1024)
            return false;

        var hashed = SHA256.HashData(Encoding.UTF8.GetBytes(presentedToken));
        ClientEntry? matched = null;
        // Compare every configured hash with constant-time comparisons.
        foreach (var item in _entries)
            if (CryptographicOperations.FixedTimeEquals(item.Hash, hashed))
                matched = item;

        if (matched is null)
            return false;

        identity = new ClientIdentity(matched.Id, matched.Scopes);
        return true;
    }

    /// <summary>
    /// Explicit fail-closed route classification. Unknown paths do not inherit any read scope.
    /// </summary>
    public static IReadOnlyList<string>? RequiredScopes(string path)
    {
        if (Regex.IsMatch(path, @"\A/api/v1/processes/[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}/activity\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return [ProcessesRead, NetworkRead];
        return RequiredScope(path) is { } scope ? [scope] : null;
    }

    // Retained for callers that classify single-scope routes. Composite routes fail closed here.
    public static string? RequiredScope(string path)
    {
        if (path == "/api/v1/events" ||
            Regex.IsMatch(path, @"\A/api/v1/events/[a-fA-F0-9]{64}\z",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return EventsRead;
        if (path == "/api/v1/processes/events" ||
            Regex.IsMatch(path, @"\A/api/v1/processes/[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}/timeline\z",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return ProcessesRead;
        if (path is "/api/v1/network/connections" or "/api/v1/network/dns")
            return NetworkRead;
        if (path == "/api/v1/monitoring/coverage" || path == "/api/v1/monitoring/gaps")
            return CoverageRead;
        if (path == "/api/v1/storage/status" || path == "/api/v1/storage/retention")
            return StorageRead;
        return null;
    }
}
