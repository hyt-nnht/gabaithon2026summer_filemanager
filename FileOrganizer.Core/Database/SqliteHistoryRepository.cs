using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using FileOrganizer.Shared.Contracts;
using FileOrganizer.Shared.Models;
using Microsoft.Data.Sqlite;

namespace FileOrganizer.Core.Database;

/// <summary>
/// <see cref="IHistoryRepository"/>のSQLite実装。AI_IMPLEMENTATION_GUIDE.md §1.2の
/// <see cref="HistoryRecord"/>/<see cref="OperationState"/>/<see cref="OperationType"/>と、
/// §2のDDL（<c>operation_history</c>テーブル）に準拠する。
/// 仕様書§3.3「2フェーズ状態遷移（Planned→Executing→Completed/Failed、
/// Undoing→Undone/UndoFailed）」がDB上で正しく記録・更新できることを満たす。
/// </summary>
/// <remarks>
/// 呼び出しごとに<see cref="SqliteConnection"/>を開閉する（コネクションプーリングは
/// Microsoft.Data.Sqliteが内部で行う）。全クエリはパラメータ化しており、SQLインジェクションの
/// 混入経路（文字列連結によるSQL組み立て）を持たない。
/// DBスキーマ自体の作成は<see cref="DatabaseInitializer"/>の責務であり、本クラスは行わない。
/// </remarks>
public sealed class SqliteHistoryRepository : IHistoryRepository
{
    private const string SelectColumns = """
        id, operation_id, op_type, source_path, destination_path, file_size_bytes,
        file_last_modified_utc, lightweight_hash, state, error_message, created_at_utc, updated_at_utc
        """;

    private readonly string _connectionString;

    public SqliteHistoryRepository(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("接続文字列を空にすることはできません。", nameof(connectionString));

        _connectionString = connectionString;
    }

    public async Task<long> InsertAsync(HistoryRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO operation_history
                (operation_id, op_type, source_path, destination_path, file_size_bytes,
                 file_last_modified_utc, lightweight_hash, state, error_message, created_at_utc, updated_at_utc)
            VALUES
                (@operation_id, @op_type, @source_path, @destination_path, @file_size_bytes,
                 @file_last_modified_utc, @lightweight_hash, @state, @error_message, @created_at_utc, @updated_at_utc);
            """;

        AddParameter(command, "@operation_id", record.OperationId);
        AddParameter(command, "@op_type", record.OpType.ToString());
        AddParameter(command, "@source_path", record.SourcePath);
        AddParameter(command, "@destination_path", (object?)record.DestinationPath ?? DBNull.Value);
        AddParameter(command, "@file_size_bytes", record.FileSizeBytes);
        AddParameter(command, "@file_last_modified_utc", ToIso(record.FileLastModifiedUtc));
        AddParameter(command, "@lightweight_hash", record.LightweightHash);
        AddParameter(command, "@state", record.State.ToString());
        AddParameter(command, "@error_message", (object?)record.ErrorMessage ?? DBNull.Value);
        AddParameter(command, "@created_at_utc", ToIso(record.CreatedAtUtc));
        AddParameter(command, "@updated_at_utc", ToIso(record.UpdatedAtUtc));

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using var lastIdCommand = connection.CreateCommand();
        lastIdCommand.CommandText = "SELECT last_insert_rowid();";
        long newId = (long)(await lastIdCommand.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        record.Id = newId;
        return newId;
    }

    public async Task UpdateStateAsync(long id, OperationState newState, string? errorMessage = null, CancellationToken ct = default)
    {
        DateTime updatedAtUtc = DateTime.UtcNow;

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE operation_history
            SET state = @state, error_message = @error_message, updated_at_utc = @updated_at_utc
            WHERE id = @id;
            """;

        AddParameter(command, "@state", newState.ToString());
        AddParameter(command, "@error_message", (object?)errorMessage ?? DBNull.Value);
        AddParameter(command, "@updated_at_utc", ToIso(updatedAtUtc));
        AddParameter(command, "@id", id);

        int affected = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (affected == 0)
        {
            throw new KeyNotFoundException($"HistoryRecord (id={id}) が見つかりません。");
        }
    }

    public async Task<HistoryRecord?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM operation_history WHERE id = @id;";
        AddParameter(command, "@id", id);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? MapRecord(reader) : null;
    }

    public async Task<HistoryRecord?> GetByOperationIdAsync(string operationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(operationId))
            throw new ArgumentException("operationIdを空にすることはできません。", nameof(operationId));

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM operation_history WHERE operation_id = @operation_id;";
        AddParameter(command, "@operation_id", operationId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? MapRecord(reader) : null;
    }

    public async Task<IReadOnlyList<HistoryRecord>> GetRecordsByStateAsync(OperationState state, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // idx_history_state を利用する絞り込み。挿入順（id昇順）で返す。
        command.CommandText = $"SELECT {SelectColumns} FROM operation_history WHERE state = @state ORDER BY id ASC;";
        AddParameter(command, "@state", state.ToString());

        var results = new List<HistoryRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(MapRecord(reader));
        }
        return results;
    }

    public async Task<IReadOnlyList<HistoryRecord>> GetRecentAsync(int count, CancellationToken ct = default)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), count, "countは0以上である必要があります。");

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // idx_history_created_at(DESC) を利用する新しい順の取得。
        command.CommandText = $"SELECT {SelectColumns} FROM operation_history ORDER BY created_at_utc DESC, id DESC LIMIT @count;";
        AddParameter(command, "@count", count);

        var results = new List<HistoryRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(MapRecord(reader));
        }
        return results;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }

    private static void AddParameter(SqliteCommand command, string name, object value)
        => command.Parameters.Add(new SqliteParameter(name, value));

    private static string ToIso(DateTime value)
        => DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    private static DateTime FromIso(string value)
        => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static HistoryRecord MapRecord(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        OperationId = reader.GetString(1),
        OpType = Enum.Parse<OperationType>(reader.GetString(2)),
        SourcePath = reader.GetString(3),
        DestinationPath = reader.IsDBNull(4) ? null : reader.GetString(4),
        FileSizeBytes = reader.GetInt64(5),
        FileLastModifiedUtc = FromIso(reader.GetString(6)),
        LightweightHash = reader.GetString(7),
        State = Enum.Parse<OperationState>(reader.GetString(8)),
        ErrorMessage = reader.IsDBNull(9) ? null : reader.GetString(9),
        CreatedAtUtc = FromIso(reader.GetString(10)),
        UpdatedAtUtc = FromIso(reader.GetString(11)),
    };
}
