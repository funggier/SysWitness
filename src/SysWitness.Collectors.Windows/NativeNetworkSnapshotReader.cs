using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using SysWitness.Contracts;

namespace SysWitness.Collectors.Windows;

/// <summary>The narrow read-only IP Helper boundary; injectable to verify churn and failure behavior.</summary>
public interface IWindowsNetworkTableApi
{
    uint Read(bool tcp, uint addressFamily, IntPtr table, ref uint size);
}

public sealed class WindowsIpHelperApi : IWindowsNetworkTableApi
{
    public uint Read(bool tcp, uint addressFamily, IntPtr table, ref uint size)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return tcp ? GetExtendedTcpTable(table, ref size, 0, addressFamily, 5, 0) :
            GetExtendedUdpTable(table, ref size, 0, addressFamily, 1, 0);
    }

    // BOOL is a 32-bit integer. Return values are Win32 status codes, not GetLastError.
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, int order,
        uint addressFamily, uint tableClass, uint reserved);
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref uint size, int order,
        uint addressFamily, uint tableClass, uint reserved);
}

public sealed class NativeNetworkSnapshotReader(IWindowsNetworkTableApi? api = null)
{
    private readonly IWindowsNetworkTableApi _api = api ?? new WindowsIpHelperApi();
    private const uint InsufficientBuffer = 122;
    private const uint MaxBufferBytes = 16 * 1024 * 1024;
    private const int MaxAttempts = 5;

    public NetworkSnapshot Capture(CancellationToken ct = default)
    {
        var start = DateTimeOffset.UtcNow;
        var partitions = new List<NetworkSnapshotPartition>(4);
        foreach (var (protocol, family) in new[] {
            ("tcp", "ipv4"), ("tcp", "ipv6"), ("udp", "ipv4"), ("udp", "ipv6") })
        {
            ct.ThrowIfCancellationRequested();
            partitions.Add(ReadPartition(protocol, family, ct));
        }
        return new(start, DateTimeOffset.UtcNow, partitions);
    }

    private NetworkSnapshotPartition ReadPartition(string protocol, string family, CancellationToken ct)
    {
        var tcp = protocol == "tcp";
        var addressFamily = family == "ipv4" ? 2u : 23u;
        uint size = 0;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            var status = _api.Read(tcp, addressFamily, IntPtr.Zero, ref size);
            if (status != InsufficientBuffer)
                return Failed(status == 0 ? "invalid_native_buffer" : "native_error", status);
            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                if (size is < 4 or > MaxBufferBytes)
                    return Failed("native_buffer_limit", null);
                var capacity = size;
                buffer = Marshal.AllocHGlobal(checked((int)capacity));
                status = _api.Read(tcp, addressFamily, buffer, ref size);
                if (status == 0)
                {
                    if (size < 4 || size > capacity) return Failed("invalid_native_buffer", null);
                    var bytes = new byte[(int)size];
                    Marshal.Copy(buffer, bytes, 0, bytes.Length);
                    if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) > NativeNetworkTableDecoder.MaxRowsPerPartition)
                        return Failed("native_row_limit", null);
                    var rows = NativeNetworkTableDecoder.Decode(bytes, protocol, family);
                    return new(protocol, family, true, null, null, rows);
                }
                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
                if (status != InsufficientBuffer) return Failed("native_error", status);
                // pdwSize is the required size after every insufficient-buffer response.
            }
            return Failed("native_buffer_churn", InsufficientBuffer);
        }
        catch (InvalidDataException) { return Failed("invalid_native_buffer", null); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException
            or PlatformNotSupportedException)
        { return Failed("native_api_unavailable", null); }
        finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); }

        NetworkSnapshotPartition Failed(string reason, uint? error) =>
            new(protocol, family, false, error, reason, []);
    }
}

public static class NativeNetworkTableDecoder
{
    public const int MaxRowsPerPartition = 16_384;

    public static IReadOnlyList<NetworkInventoryRow> Decode(
        ReadOnlySpan<byte> table, string protocol, string family)
    {
        if (protocol is not ("tcp" or "udp") || family is not ("ipv4" or "ipv6"))
            throw new ArgumentException("Unsupported IP Helper table.");
        var tcp = protocol == "tcp";
        var v6 = family == "ipv6";
        // SDK fields are DWORD or byte[16], all 4-byte aligned on Windows x86/x64/arm64.
        // Header DWORD + inline first row has offset 4 for each of these OWNER_PID layouts.
        var stride = tcp ? (v6 ? 56 : 24) : (v6 ? 28 : 12);
        if (table.Length < 4) throw new InvalidDataException("Missing native table header.");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        if (count > MaxRowsPerPartition || count > (uint)((table.Length - 4) / stride))
            throw new InvalidDataException("Native table count exceeds returned buffer.");
        var rows = new List<NetworkInventoryRow>((int)count);
        for (var i = 0; i < (int)count; i++)
        {
            var row = table.Slice(4 + i * stride, stride);
            var state = tcp ? U32(row, v6 ? 48 : 0) : (uint?)null;
            var localAddress = new IPAddress(row.Slice(v6 ? 0 : (tcp ? 4 : 0), v6 ? 16 : 4)).ToString();
            var localPort = Port(row, v6 ? 20 : (tcp ? 8 : 4));
            var localScope = v6 ? BinaryPrimitives.ReadUInt32BigEndian(row[16..]) : 0;
            var remoteAddress = tcp && state != 2
                ? new IPAddress(row.Slice(v6 ? 24 : 12, v6 ? 16 : 4)).ToString() : null;
            var remotePort = tcp && state != 2 ? Port(row, v6 ? 44 : 16) : (ushort?)null;
            var remoteScope = tcp && v6 && state != 2
                ? BinaryPrimitives.ReadUInt32BigEndian(row[40..]) : (uint?)null;
            var pid = U32(row, tcp ? (v6 ? 52 : 20) : (v6 ? 24 : 8));
            rows.Add(new(protocol, family, pid, localAddress, localPort, localScope,
                remoteAddress, remotePort, remoteScope, state, state is uint value ? StateName(value) : null));
        }
        return rows;
    }

    private static uint U32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static ushort Port(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);

    public static string StateName(uint state) => state switch
    {
        1 => "Closed", 2 => "Listen", 3 => "SynSent", 4 => "SynReceived", 5 => "Established",
        6 => "FinWait1", 7 => "FinWait2", 8 => "CloseWait", 9 => "Closing", 10 => "LastAck",
        11 => "TimeWait", 12 => "DeleteTcb", _ => "Unknown(" + state.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")"
    };
}
