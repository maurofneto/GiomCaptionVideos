using Domain.Interfaces;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.Persistence.SqlServer;

public class SqlProgressoRepository : IProgressoRepository
{
    private readonly string _connectionString;

    public SqlProgressoRepository(IConfiguration configuration)
    {
        _connectionString = configuration["ConnectionStrings:Default"]
            ?? throw new InvalidOperationException("ConnectionStrings:Default não configurado");
    }

    public async Task<string?> GetLastPageTokenAsync(string channelKey, string playlistId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(
            "SELECT LastPageToken FROM ProcessamentoProgresso WHERE ChannelKey=@ChannelKey AND PlaylistId=@PlaylistId",
            conn);
        cmd.Parameters.AddWithValue("@ChannelKey", channelKey);
        cmd.Parameters.AddWithValue("@PlaylistId", playlistId);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result != DBNull.Value && result != null ? result.ToString() : null;
    }

    public async Task<DateTime?> GetLastProcessedDateAsync(string channelKey, string playlistId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(
            "SELECT LastProcessedDate FROM ProcessamentoProgresso WHERE ChannelKey=@ChannelKey AND PlaylistId=@PlaylistId",
            conn);
        cmd.Parameters.AddWithValue("@ChannelKey", channelKey);
        cmd.Parameters.AddWithValue("@PlaylistId", playlistId);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result != DBNull.Value && result != null ? (DateTime?)result : null;
    }

    public async Task SaveProgressoAsync(string channelKey, string playlistId, string? pageToken, DateTime? maxProcessedDate, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(
            @"MERGE ProcessamentoProgresso AS target
              USING (VALUES (@ChannelKey, @PlaylistId, @LastPageToken, @LastProcessedDate, GETDATE()))
                AS source (ChannelKey, PlaylistId, LastPageToken, LastProcessedDate, LastExecutionDate)
              ON target.ChannelKey = source.ChannelKey AND target.PlaylistId = source.PlaylistId
              WHEN MATCHED THEN UPDATE SET
                LastPageToken=source.LastPageToken,
                LastProcessedDate=source.LastProcessedDate,
                LastExecutionDate=source.LastExecutionDate
              WHEN NOT MATCHED THEN
                INSERT (ChannelKey, PlaylistId, LastPageToken, LastProcessedDate, LastExecutionDate)
                VALUES (source.ChannelKey, source.PlaylistId, source.LastPageToken, source.LastProcessedDate, source.LastExecutionDate);",
            conn);

        cmd.Parameters.AddWithValue("@ChannelKey", channelKey);
        cmd.Parameters.AddWithValue("@PlaylistId", playlistId);
        cmd.Parameters.AddWithValue("@LastPageToken", (object?)pageToken ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@LastProcessedDate", (object?)maxProcessedDate ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }
}
