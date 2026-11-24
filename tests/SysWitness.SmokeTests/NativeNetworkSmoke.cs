using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using SysWitness.Collectors.Windows;
using SysWitness.Contracts;
using SysWitness.Core;
using SysWitness.Storage;

internal static class NativeNetworkSmoke
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    public static async Task VerifyAsync(string path)
    {
        VerifySynthetic();
        await SeedAsync(path);
        var store = new SqliteEventStore(path);
        var first = await store.GetNetworkInventoryAsync(new EventQuery(Limit: 1));
        Check(first.Returned == 1 && first.NextCursor is not null &&
            first.Items[0] is { Change: "notObserved", ProcessAttribution: "pidOnly", Quality: "derived" },
            "Network inventory pages contain observation diffs");
        var second = await store.GetNetworkInventoryAsync(new EventQuery(Limit: 2, Cursor: first.NextCursor));
        Check(second.Returned == 1 && second.NextCursor is null &&
            second.Items[0] is { Change: "firstObserved", Endpoint: { Protocol: "udp", RemoteAddress: null,
                RemotePort: null, TcpState: null } }, "UDP inventory never invents a peer");
        Check((await store.GetNetworkInventoryAsync(new EventQuery(Provider: "Other"))).Returned == 0,
            "Inventory uses source filters before paging");
        var broken = LocalNetworkEvidence.Create("firstObserved", Row("tcp") with { TcpState = 5 },
            Epoch, Epoch, "synthetic-host") with { RawXml = "<!DOCTYPE x [<!ENTITY a SYSTEM 'file:///x'>]><x>&a;</x>" };
        Check(LocalNetworkEvidence.Decode(broken) is { PayloadStatus: "unavailable", Endpoint: null },
            "Malformed/DTD payload does not decode");
        Check(LocalNetworkEvidence.Decode(broken with { Provider = "Other" }) is null,
            "Unrelated providers are excluded");
    }

    public static async Task SeedAsync(string path)
    {
        var store = new SqliteEventStore(path);
        await store.InitializeAsync();
        var endpoint = Row("udp");
        await store.InsertAsync([
            LocalNetworkEvidence.Create("firstObserved", endpoint, Epoch, Epoch, "synthetic-host"),
            LocalNetworkEvidence.Create("notObserved", endpoint, Epoch.AddSeconds(3), Epoch.AddSeconds(3), "synthetic-host")
        ]);
    }

    public static void VerifySynthetic()
    {
        foreach (var (protocol, family, stride) in new[] {
            ("tcp", "ipv4", 24), ("udp", "ipv4", 12), ("tcp", "ipv6", 56), ("udp", "ipv6", 28) })
        {
            var buffer = Fixture(protocol, family);
            Check(buffer.Length == 4 + stride, "Native layout size");
            var row = NativeNetworkTableDecoder.Decode(buffer, protocol, family).Single();
            Check(row.ProcessId == 123 && row.LocalPort == 50000 &&
                row.LocalAddress == (family == "ipv4" ? "127.0.0.1" : "::1"), "Native address/port/PID decode");
            Check(protocol == "udp" ? row.RemoteAddress is null && row.TcpState is null :
                row.RemotePort == 443 && row.TcpState == 5, "Native TCP versus UDP semantics");
            ExpectInvalid(buffer[..^1], protocol, family);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, uint.MaxValue);
            ExpectInvalid(buffer, protocol, family);
        }
        var excessive = new byte[4 + 16_385 * 12];
        BinaryPrimitives.WriteUInt32LittleEndian(excessive, 16_385);
        ExpectInvalid(excessive, "udp", "ipv4");
        var maxPort = Fixture("udp", "ipv4");
        BinaryPrimitives.WriteUInt16BigEndian(maxPort.AsSpan(8), ushort.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(maxPort.AsSpan(12), 0);
        var zeroOwner = NativeNetworkTableDecoder.Decode(maxPort, "udp", "ipv4").Single();
        Check(zeroOwner is { LocalPort: ushort.MaxValue, ProcessId: 0 }, "Unsigned port and unavailable PID");
        var zeroEvidence = LocalNetworkEvidence.Create("firstObserved", zeroOwner, Epoch, Epoch, "synthetic-host");
        Check(LocalNetworkEvidence.Decode(zeroEvidence)?.ProcessAttribution == "unavailable", "PID zero is not an owner identity");
        var listen = Fixture("tcp", "ipv4");
        BinaryPrimitives.WriteUInt32LittleEndian(listen.AsSpan(4), 2);
        Check(NativeNetworkTableDecoder.Decode(listen, "tcp", "ipv4").Single()
            is { RemoteAddress: null, RemotePort: null }, "LISTEN remote values have no meaning");
        var unknown = Fixture("tcp", "ipv6");
        BinaryPrimitives.WriteUInt32LittleEndian(unknown.AsSpan(52), 0);
        Check(NativeNetworkTableDecoder.Decode(unknown, "tcp", "ipv6").Single()
            is { TcpState: 0, TcpStateName: "Unknown(0)" }, "Unknown state is retained");
        var scoped = Fixture("udp", "ipv6");
        BinaryPrimitives.WriteUInt32BigEndian(scoped.AsSpan(20), 17);
        Check(NativeNetworkTableDecoder.Decode(scoped, "udp", "ipv6").Single().LocalScopeId == 17,
            "Documented IPv6 scope network order");
        Check(NativeNetworkTableDecoder.Decode(new byte[4], "udp", "ipv4").Count == 0, "Empty table");
        var fake = new FakeApi();
        var reader = new NativeNetworkSnapshotReader(fake);
        var capture = reader.Capture();
        Check(capture.Partitions.Count == 4 && capture.Partitions.Count(x => x.Succeeded) == 3 &&
            capture.Partitions.Single(x => x.Protocol == "udp" && x.AddressFamily == "ipv6").NativeError == 5,
            "Independent family failure and bounded buffer retry");
        Check(fake.Attempts == 3, "Buffer can grow between probe and fill");
        var churn = new ChurnApi();
        Check(new NativeNetworkSnapshotReader(churn).Capture().Partitions.All(x =>
            !x.Succeeded && x.FailureReason == "native_buffer_churn") && churn.Calls == 24,
            "Insufficient-buffer retries are bounded independently");
        var cap = new ChurnApi { RequiredBytes = uint.MaxValue };
        Check(new NativeNetworkSnapshotReader(cap).Capture().Partitions.All(x =>
            !x.Succeeded && x.FailureReason == "native_buffer_limit") && cap.Calls == 4,
            "Oversized native allocation rejected before allocation");

        var tracker = new NetworkSnapshotDiffer();
        var good = Capture(Row("tcp"));
        var initial = tracker.Compare(good, "synthetic-host");
        Check(initial.Count == 1 && LocalNetworkEvidence.Decode(initial[0])?.Change == "firstObserved",
            "First successful inventory persisted");
        Check(tracker.Compare(good, "synthetic-host").Count == 0, "Unchanged snapshots are quiet");
        Check(tracker.Compare(new NetworkSnapshot(Epoch, Epoch, [
            new("tcp", "ipv4", false, 5, "native_error", [])]), "synthetic-host").Count == 0,
            "Failed partition does not remove earlier inventory");
        var changed = tracker.Compare(Capture(Row("tcp") with { TcpState = 8, TcpStateName = "CloseWait" }),
            "synthetic-host");
        Check(changed.Count == 1 && LocalNetworkEvidence.Decode(changed[0])?.Change == "changed",
            "State change is a snapshot change");
        var gone = tracker.Compare(Capture(), "synthetic-host");
        Check(gone.Count == 1 && LocalNetworkEvidence.Decode(gone[0])?.Change == "notObserved",
            "Successful empty table yields disappearance observation");
        Check(tracker.Compare(Capture(), "synthetic-host").Count == 0, "No repeated disappearance");
    }

    public static void VerifyLive()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var family in new[] { AddressFamily.InterNetwork, AddressFamily.InterNetworkV6 })
        {
            if (family == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6) continue;
            var address = family == AddressFamily.InterNetwork ? IPAddress.Loopback : IPAddress.IPv6Loopback;
            var listener = new TcpListener(address, 0);
            listener.Start();
            try
            {
                using var udp = new UdpClient(family);
                udp.Client.Bind(new IPEndPoint(address, 0));
                using var client = new TcpClient(family);
                client.Connect((IPEndPoint)listener.LocalEndpoint);
                using var accepted = listener.AcceptTcpClient();
                var tcpPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                var udpPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
                var capture = new NativeNetworkSnapshotReader().Capture();
                var ip = family == AddressFamily.InterNetwork ? "ipv4" : "ipv6";
                var tcp = capture.Partitions.Single(x => x.Protocol == "tcp" && x.AddressFamily == ip);
                var udpRows = capture.Partitions.Single(x => x.Protocol == "udp" && x.AddressFamily == ip);
                Check(tcp.Succeeded && udpRows.Succeeded, "Native loopback partitions readable");
                Check(tcp.Rows.Any(x => x.ProcessId == (uint)Environment.ProcessId &&
                    x.LocalPort == tcpPort && x.TcpState == 2), "Own TCP listener appears");
                Check(tcp.Rows.Any(x => x.ProcessId == (uint)Environment.ProcessId &&
                    x.LocalPort == tcpPort && x.TcpState == 5), "Own established TCP appears");
                Check(udpRows.Rows.Any(x => x.ProcessId == (uint)Environment.ProcessId &&
                    x.LocalPort == udpPort && x.RemoteAddress is null), "Own UDP endpoint appears");
            }
            finally { listener.Stop(); }
        }
    }

    private static NetworkInventoryRow Row(string protocol) => new(protocol, "ipv4", 123,
        "127.0.0.1", 50000, 0, protocol == "tcp" ? "127.0.0.1" : null,
        protocol == "tcp" ? (ushort)443 : null, null, protocol == "tcp" ? 5u : null,
        protocol == "tcp" ? "Established" : null);

    private static NetworkSnapshot Capture(params NetworkInventoryRow[] rows) => new(Epoch, Epoch,
        [new("tcp", "ipv4", true, null, null, rows)]);

    private static byte[] Fixture(string protocol, string family)
    {
        var tcp = protocol == "tcp";
        var v6 = family == "ipv6";
        var data = new byte[4 + (tcp ? (v6 ? 56 : 24) : (v6 ? 28 : 12))];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 1);
        var row = data.AsSpan(4);
        var addr = v6 ? IPAddress.IPv6Loopback.GetAddressBytes() : IPAddress.Loopback.GetAddressBytes();
        if (v6)
        {
            addr.CopyTo(row);
            BinaryPrimitives.WriteUInt16BigEndian(row[20..], 50000);
            if (tcp)
            {
                addr.CopyTo(row[24..]);
                BinaryPrimitives.WriteUInt16BigEndian(row[44..], 443);
                BinaryPrimitives.WriteUInt32LittleEndian(row[48..], 5);
            }
        }
        else
        {
            addr.CopyTo(row[(tcp ? 4 : 0)..]);
            BinaryPrimitives.WriteUInt16BigEndian(row[(tcp ? 8 : 4)..], 50000);
            if (tcp)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(row, 5);
                addr.CopyTo(row[12..]);
                BinaryPrimitives.WriteUInt16BigEndian(row[16..], 443);
            }
        }
        BinaryPrimitives.WriteUInt32LittleEndian(row[(tcp ? (v6 ? 52 : 20) : (v6 ? 24 : 8))..], 123);
        return data;
    }

    private static void ExpectInvalid(byte[] buffer, string protocol, string family)
    {
        try { NativeNetworkTableDecoder.Decode(buffer, protocol, family); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("FAIL: Truncated/overflow table accepted");
    }

    private sealed class ChurnApi : IWindowsNetworkTableApi
    {
        public int Calls { get; private set; }
        public uint RequiredBytes { get; init; } = 4;
        public uint Read(bool tcp, uint family, IntPtr buffer, ref uint size)
        { Calls++; size = RequiredBytes; return 122; }
    }

    private sealed class FakeApi : IWindowsNetworkTableApi
    {
        public int Attempts { get; private set; }
        public uint Read(bool tcp, uint family, IntPtr buffer, ref uint size)
        {
            if (!tcp && family == 23) return 5;
            if (tcp && family == 2)
            {
                Attempts++;
                if (Attempts <= 2) { size = (uint)(Attempts == 1 ? 4 : 28); return 122; }
                var fixture = Fixture("tcp", "ipv4");
                Marshal.Copy(fixture, 0, buffer, fixture.Length); size = (uint)fixture.Length; return 0;
            }
            if (buffer == IntPtr.Zero) { size = 4; return 122; }
            Marshal.WriteInt32(buffer, 0); size = 4; return 0;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
    }
}
