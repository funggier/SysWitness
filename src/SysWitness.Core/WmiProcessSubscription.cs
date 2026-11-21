using System.Management;
using System.Runtime.Versioning;
using System.Threading.Channels;
using SysWitness.Contracts;

namespace SysWitness.Core;

/// <summary>Starts temporary local WMI subscriptions and keeps a bounded queue of captured events.</summary>
[SupportedOSPlatform("windows")]
public sealed class WmiProcessSubscription : IDisposable
{
    private readonly ManagementEventWatcher _start;
    private readonly ManagementEventWatcher _stop;
    private readonly string _computer = Environment.MachineName;
    private readonly Channel<ObservedEvent> _events = Channel.CreateBounded<ObservedEvent>(
        new BoundedChannelOptions(2_048) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private long _dropped;
    private string? _failure;

    private WmiProcessSubscription()
    {
        _start = CreateWatcher("Win32_ProcessStartTrace", LocalProcessEvidence.Start);
        _stop = CreateWatcher("Win32_ProcessStopTrace", LocalProcessEvidence.Exit);
    }

    public string? Failure => Volatile.Read(ref _failure);
    public long Dropped => Interlocked.Read(ref _dropped);
    public ChannelReader<ObservedEvent> Events => _events.Reader;

    public static WmiProcessSubscription Open()
    {
        var subscription = new WmiProcessSubscription();
        try
        {
            subscription._start.Start();
            subscription._stop.Start();
            return subscription;
        }
        catch
        {
            subscription.Dispose();
            throw;
        }
    }

    private ManagementEventWatcher CreateWatcher(string eventClass, int kind)
    {
        var watcher = new ManagementEventWatcher(new ManagementScope(@"root\cimv2"), new WqlEventQuery(eventClass));
        watcher.EventArrived += (_, args) => Receive(args, kind);
        watcher.Stopped += (_, args) => Interlocked.CompareExchange(ref _failure,
            args.Status == ManagementStatus.AccessDenied ? "wmi_access_denied" : "wmi_subscription_stopped", null);
        return watcher;
    }

    private void Receive(EventArrivedEventArgs args, int kind)
    {
        try
        {
            using var data = args.NewEvent;
            var pid = (uint)data["ProcessID"];
            var time = DateTimeOffset.FromFileTime(checked((long)(ulong)data["TIME_CREATED"])).ToUniversalTime();
            var parent = kind == LocalProcessEvidence.Start ? (uint?)data["ParentProcessID"] : null;
            var exitStatus = kind == LocalProcessEvidence.Exit ? (uint?)data["ExitStatus"] : null;
            var observation = LocalProcessEvidence.Create(LocalProcessEvidence.WmiChannel, kind,
                _computer, time, DateTimeOffset.UtcNow, pid, data["ProcessName"] as string,
                parent, exitStatus);
            if (!_events.Writer.TryWrite(observation))
                Interlocked.Increment(ref _dropped);
        }
        catch (Exception error) when (error is ManagementException or ArgumentException or
            InvalidCastException or NullReferenceException or OverflowException)
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    public void Dispose()
    {
        Close(_start);
        Close(_stop);
        _events.Writer.TryComplete();
    }

    private static void Close(ManagementEventWatcher watcher)
    {
        try { watcher.Stop(); }
        catch (Exception error) when (error is ManagementException or System.Runtime.InteropServices.COMException) { }
        watcher.Dispose();
    }
}
