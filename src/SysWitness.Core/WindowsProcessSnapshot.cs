using System.ComponentModel;
using System.Diagnostics;
using SysWitness.Contracts;

namespace SysWitness.Core;

public sealed record ProcessSample(uint ProcessId, string? Name, DateTimeOffset? StartedAtUtc);

public static class WindowsProcessSnapshot
{
    public static IReadOnlyDictionary<uint, ProcessSample> Read(DateTimeOffset sampleStartedAt)
    {
        var samples = new Dictionary<uint, ProcessSample>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                var id = (uint)process.Id;
                var started = TryReadTime(() => new DateTimeOffset(process.StartTime.ToUniversalTime()));
                if (started > sampleStartedAt)
                    started = null; // The process may have changed since Windows enumerated the PID.
                samples.Add(id, new ProcessSample(id, TryRead(() => process.ProcessName), started));
            }
        }

        if (samples.Count == 0)
            throw new InvalidOperationException("Windows returned an empty process inventory.");
        return samples;
    }

    public static IReadOnlyList<ObservedEvent> Compare(
        IReadOnlyDictionary<uint, ProcessSample> previous,
        IReadOnlyDictionary<uint, ProcessSample> current,
        DateTimeOffset observedAt,
        string computer)
    {
        var changes = new List<ObservedEvent>();
        foreach (var (pid, oldSample) in previous)
        {
            if (!current.TryGetValue(pid, out var sample) || ReusedPid(oldSample, sample))
                changes.Add(Observation(oldSample, LocalProcessEvidence.Exit));
        }

        foreach (var (pid, sample) in current)
        {
            if (!previous.TryGetValue(pid, out var oldSample) || ReusedPid(oldSample, sample))
                changes.Add(Observation(sample, LocalProcessEvidence.Start));
        }
        return changes;

        ObservedEvent Observation(ProcessSample sample, int kind) =>
            LocalProcessEvidence.Create(LocalProcessEvidence.SnapshotChannel, kind, computer,
                observedAt, observedAt, sample.ProcessId, sample.Name, startedAt: sample.StartedAtUtc);
    }

    private static bool ReusedPid(ProcessSample oldSample, ProcessSample newSample) =>
        oldSample.StartedAtUtc is { } previous && newSample.StartedAtUtc is { } current &&
        previous != current;

    private static T? TryRead<T>(Func<T> read) where T : class
    {
        try { return read(); }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    private static DateTimeOffset? TryReadTime(Func<DateTimeOffset> read)
    {
        try { return read(); }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }
}
