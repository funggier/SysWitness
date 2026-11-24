using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Xml;
using System.Xml.Linq;
using SysWitness.Contracts;

namespace SysWitness.Core;

public static class LocalNetworkEvidence
{
    public const string SnapshotChannel = "WindowsNetworkSnapshot";
    public const string SnapshotProvider = "Windows.IPHelper";
    public const string Category = "windows.network.inventory";
    private static readonly XNamespace Ns = "urn:syswitness:network-observation:v1";

    public static ObservedEvent Create(string change, NetworkInventoryRow row,
        DateTimeOffset captureStartedAt, DateTimeOffset captureCompletedAt, string computer)
    {
        var code = ChangeCode(change);
        if (captureCompletedAt < captureStartedAt) throw new ArgumentException("Invalid capture window.");
        var xml = new XElement(Ns + "NetworkObservation",
            new XAttribute("change", change),
            new XElement(Ns + "CaptureStartedAtUtc", captureStartedAt.ToUniversalTime()),
            new XElement(Ns + "CaptureCompletedAtUtc", captureCompletedAt.ToUniversalTime()),
            new XElement(Ns + "Computer", computer),
            new XElement(Ns + "Protocol", row.Protocol),
            new XElement(Ns + "AddressFamily", row.AddressFamily),
            new XElement(Ns + "ProcessId", row.ProcessId),
            new XElement(Ns + "LocalAddress", row.LocalAddress),
            new XElement(Ns + "LocalPort", row.LocalPort),
            new XElement(Ns + "LocalScopeId", row.LocalScopeId),
            row.RemoteAddress is null ? null : new XElement(Ns + "RemoteAddress", row.RemoteAddress),
            row.RemotePort is null ? null : new XElement(Ns + "RemotePort", row.RemotePort.Value),
            row.RemoteScopeId is null ? null : new XElement(Ns + "RemoteScopeId", row.RemoteScopeId.Value),
            row.TcpState is null ? null : new XElement(Ns + "TcpState", row.TcpState.Value))
            .ToString(SaveOptions.DisableFormatting);
        return new(EventIdentity.FromXml(SnapshotChannel, xml), captureCompletedAt, captureCompletedAt,
            Category, SnapshotChannel, SnapshotProvider, code, null, computer, xml);
    }

    public static NetworkInventoryObservation? Decode(ObservedEvent evidence)
    {
        if (evidence.Channel != SnapshotChannel || evidence.Provider != SnapshotProvider ||
            evidence.SourceEventId is < 1 or > 3) return null;
        var change = evidence.SourceEventId switch { 1 => "firstObserved", 2 => "changed", _ => "notObserved" };
        NetworkInventoryRow? row = null;
        DateTimeOffset? started = null, completed = null;
        try
        {
            using var input = new StringReader(evidence.RawXml);
            using var reader = XmlReader.Create(input, new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65_536 });
            var root = XElement.Load(reader);
            if (root.Name != Ns + "NetworkObservation" || root.Attribute("change")?.Value != change)
                throw new InvalidDataException();
            string? Field(string name) => root.Elements(Ns + name).SingleOrDefault()?.Value;
            uint Number(string name) => uint.TryParse(Field(name), NumberStyles.None,
                CultureInfo.InvariantCulture, out var n) ? n : throw new InvalidDataException();
            uint? OptionalNumber(string name) => Field(name) is null ? null : Number(name);
            ushort Port(string name) => Number(name) is <= ushort.MaxValue and var n ? (ushort)n :
                throw new InvalidDataException();
            DateTimeOffset Time(string name) => DateTimeOffset.TryParse(Field(name), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t :
                throw new InvalidDataException();
            var protocol = Field("Protocol");
            var family = Field("AddressFamily");
            if (protocol is not ("tcp" or "udp") || family is not ("ipv4" or "ipv6"))
                throw new InvalidDataException();
            string Address(string name)
            {
                if (!IPAddress.TryParse(Field(name), out var ip) ||
                    ip.AddressFamily != (family == "ipv4" ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6) ||
                    (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.ScopeId != 0))
                    throw new InvalidDataException();
                return ip.ToString();
            }
            var state = OptionalNumber("TcpState");
            var remoteAddress = Field("RemoteAddress") is null ? null : Address("RemoteAddress");
            var remotePort = Field("RemotePort") is null ? null : (ushort?)Port("RemotePort");
            var scope = Number("LocalScopeId");
            var remoteScope = OptionalNumber("RemoteScopeId");
            if ((family == "ipv4" && (scope != 0 || remoteScope is not null)) ||
                (protocol == "udp" && (state is not null || remoteAddress is not null ||
                    remotePort is not null || remoteScope is not null)) ||
                (protocol == "tcp" && (state is null || (state == 2 &&
                    (remoteAddress is not null || remotePort is not null || remoteScope is not null)) ||
                    (state != 2 && (remoteAddress is null || remotePort is null ||
                        (family == "ipv6" && remoteScope is null))))))
                throw new InvalidDataException();
            started = Time("CaptureStartedAtUtc");
            completed = Time("CaptureCompletedAtUtc");
            if (completed < started) throw new InvalidDataException();
            row = new(protocol, family, Number("ProcessId"), Address("LocalAddress"), Port("LocalPort"), scope,
                remoteAddress, remotePort, remoteScope, state, state is uint n ? StateName(n) : null);
        }
        catch (Exception error) when (error is XmlException or InvalidDataException or InvalidOperationException)
        {
            started = completed = null;
        }
        return new(evidence.EventId, evidence.OccurredAtUtc, evidence.ObservedAtUtc,
            new(evidence.Channel, evidence.Provider, evidence.SourceEventId, evidence.SourceRecordId, evidence.Computer),
            row is null ? "unavailable" : "decoded", change, "derived",
            row is null || row.ProcessId == 0 ? "unavailable" : "pidOnly", row, started, completed);
    }

    private static int ChangeCode(string change) => change switch
    {
        "firstObserved" => 1, "changed" => 2, "notObserved" => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(change))
    };

    public static string StateName(uint state) => state switch
    {
        1 => "Closed", 2 => "Listen", 3 => "SynSent", 4 => "SynReceived", 5 => "Established",
        6 => "FinWait1", 7 => "FinWait2", 8 => "CloseWait", 9 => "Closing", 10 => "LastAck",
        11 => "TimeWait", 12 => "DeleteTcb", _ => "Unknown(" + state.ToString(CultureInfo.InvariantCulture) + ")"
    };
}

/// <summary>Only successful partitions advance. A missing row is an observation, not proof of socket closure.</summary>
public sealed class NetworkSnapshotDiffer
{
    private sealed record Key(string Protocol, string Family, uint Pid, string Local, ushort LocalPort,
        uint LocalScope, string? Remote, ushort? RemotePort, uint? RemoteScope);
    private readonly Dictionary<(string, string), Dictionary<Key, NetworkInventoryRow>> _previous = new();

    public IReadOnlyList<ObservedEvent> Compare(NetworkSnapshot capture, string computer)
    {
        var changes = new List<ObservedEvent>();
        foreach (var partition in capture.Partitions.Where(x => x.Succeeded))
        {
            var partitionKey = (partition.Protocol, partition.AddressFamily);
            var current = new Dictionary<Key, NetworkInventoryRow>();
            foreach (var row in partition.Rows)
            {
                if (row.Protocol != partition.Protocol || row.AddressFamily != partition.AddressFamily)
                    throw new ArgumentException("Endpoint outside capture partition.");
                current[For(row)] = row;
            }
            _previous.TryGetValue(partitionKey, out var previous);
            foreach (var (key, row) in current)
                if (previous is null || !previous.TryGetValue(key, out var old))
                    Add("firstObserved", row);
                else if (row != old) Add("changed", row);
            if (previous is not null)
                foreach (var (key, row) in previous)
                    if (!current.ContainsKey(key)) Add("notObserved", row);
            _previous[partitionKey] = current;
        }
        return changes;

        void Add(string change, NetworkInventoryRow row) => changes.Add(LocalNetworkEvidence.Create(change, row,
            capture.StartedAtUtc, capture.CompletedAtUtc, computer));
    }

    private static Key For(NetworkInventoryRow r) => new(r.Protocol, r.AddressFamily, r.ProcessId,
        r.LocalAddress, r.LocalPort, r.LocalScopeId, r.RemoteAddress, r.RemotePort, r.RemoteScopeId);
}
