using Microsoft.Data.Sqlite;
using SysWitness.Core;

namespace SysWitness.Storage;

public sealed record ProcessProjectionStatus(int Version, long EligibleEvents, long IndexedEvents, long PendingEvents);

public sealed partial class SqliteEventStore
{
    private const int ProcessProjectionVersion = 1;

    private async Task InitializeProcessProjectionAsync(CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await using (var schema = db.CreateCommand())
        {
            schema.CommandText = """
                CREATE TABLE IF NOT EXISTS process_identity_projection (
                    event_id TEXT PRIMARY KEY NOT NULL REFERENCES events(event_id) ON DELETE CASCADE,
                    computer TEXT NULL,
                    process_guid TEXT NULL,
                    parent_guid TEXT NULL,
                    version INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_process_projection_subject
                    ON process_identity_projection(process_guid,computer,event_id);
                CREATE INDEX IF NOT EXISTS ix_process_projection_parent
                    ON process_identity_projection(parent_guid,computer,event_id);
                """;
            await schema.ExecuteNonQueryAsync(ct);
        }

        // Restartable derived migration: only missing/stale rows are parsed, in bounded batches.
        // Raw events and source checkpoints are never changed by this backfill.
        while (true)
        {
            await _writeLock.WaitAsync(ct);
            try
            {
                await using var tx = (SqliteTransaction)await db.BeginTransactionAsync(ct);
                var batch = new List<(string Id, string? Computer, string Xml)>();
                await using (var select = db.CreateCommand())
                {
                    select.Transaction = tx;
                    select.CommandText = """
                        SELECT e.event_id,e.computer,e.raw_xml FROM events e
                        LEFT JOIN process_identity_projection p ON p.event_id=e.event_id
                        WHERE e.channel=$channel AND e.provider=$provider
                          AND (p.event_id IS NULL OR p.version<>$version)
                        ORDER BY e.event_id LIMIT 64;
                        """;
                    select.Parameters.AddWithValue("$channel", WindowsProcessEventDecoder.SysmonChannel);
                    select.Parameters.AddWithValue("$provider", WindowsProcessEventDecoder.SysmonProvider);
                    select.Parameters.AddWithValue("$version", ProcessProjectionVersion);
                    await using var reader = await select.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                        batch.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2)));
                }
                if (batch.Count == 0) return;
                foreach (var row in batch)
                    await WriteProcessProjectionAsync(db, tx, row.Id, row.Computer, row.Xml, ct);
                await tx.CommitAsync(ct);
            }
            finally { _writeLock.Release(); }
        }
    }

    private static async Task WriteProcessProjectionAsync(SqliteConnection db, SqliteTransaction tx,
        string eventId, string? computer, string xml, CancellationToken ct)
    {
        var identity = SysmonProcessIdentity.Read(xml);
        await using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO process_identity_projection(event_id,computer,process_guid,parent_guid,version)
            VALUES($id,$computer,$subject,$parent,$version)
            ON CONFLICT(event_id) DO UPDATE SET computer=excluded.computer,
                process_guid=excluded.process_guid,parent_guid=excluded.parent_guid,version=excluded.version;
            """;
        cmd.Parameters.AddWithValue("$id", eventId);
        cmd.Parameters.AddWithValue("$computer", (object?)computer ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$subject", (object?)identity.Subject?.ToString("D") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$parent", (object?)identity.Parent?.ToString("D") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$version", ProcessProjectionVersion);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static Task ProjectInsertedEventAsync(SqliteConnection db, SqliteTransaction tx,
        SysWitness.Contracts.ObservedEvent evidence, CancellationToken ct)
    {
        if (evidence.Channel != WindowsProcessEventDecoder.SysmonChannel ||
            evidence.Provider != WindowsProcessEventDecoder.SysmonProvider) return Task.CompletedTask;
        return WriteProcessProjectionAsync(db, tx, evidence.EventId, evidence.Computer, evidence.RawXml, ct);
    }

    public async Task<ProcessProjectionStatus> GetProcessProjectionStatusAsync(CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM events WHERE channel=$channel AND provider=$provider),
              (SELECT COUNT(*) FROM process_identity_projection WHERE version=$version),
              (SELECT COUNT(*) FROM events e LEFT JOIN process_identity_projection p ON p.event_id=e.event_id
                WHERE e.channel=$channel AND e.provider=$provider AND (p.event_id IS NULL OR p.version<>$version));
            """;
        cmd.Parameters.AddWithValue("$channel", WindowsProcessEventDecoder.SysmonChannel);
        cmd.Parameters.AddWithValue("$provider", WindowsProcessEventDecoder.SysmonProvider);
        cmd.Parameters.AddWithValue("$version", ProcessProjectionVersion);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new(ProcessProjectionVersion, reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }
}
