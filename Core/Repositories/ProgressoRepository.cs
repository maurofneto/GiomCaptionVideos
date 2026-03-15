using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.Repositories;

public class ProgressoRepository
{
    private readonly string ConnectionString;

    public ProgressoRepository(IConfiguration configuration)
    {
        ConnectionString = configuration["ConnectionStrings:Default"]
                ?? throw new InvalidOperationException("ConnectionStrings:Default não configurado"); ;
    }

    public async Task<string> GetLastPageTokenAsync(string channelKey, string playlistId)
    {
        using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        using var cmd = new SqlCommand(
            @"SELECT LastPageToken 
          FROM ProcessamentoProgresso 
          WHERE ChannelKey = @ChannelKey AND PlaylistId = @PlaylistId",
            conn);

        cmd.Parameters.AddWithValue("@ChannelKey", channelKey);
        cmd.Parameters.AddWithValue("@PlaylistId", playlistId);

        var result = await cmd.ExecuteScalarAsync();
        return result != DBNull.Value ? result.ToString() : null;
    }

    public async Task<DateTime?> GetLastProcessedDateAsync(string channelKey, string playlistId)
    {
        using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        using var cmd = new SqlCommand(
            @"SELECT LastProcessedDate 
              FROM ProcessamentoProgresso 
              WHERE ChannelKey = @ChannelKey AND PlaylistId = @PlaylistId",
            conn);

        cmd.Parameters.AddWithValue("@ChannelKey", channelKey);
        cmd.Parameters.AddWithValue("@PlaylistId", playlistId);

        var result = await cmd.ExecuteScalarAsync();
        return result != DBNull.Value ? (DateTime?)result : null;
    }

    public async Task SaveProgressoAsync(string channelKey, string playlistId, string token, DateTime? maxProcessedDate)
    {
        using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        using var cmd = new SqlCommand(
            @"MERGE ProcessamentoProgresso AS target
          USING (VALUES (@ChannelKey, @PlaylistId, @LastPageToken, @LastProcessedDate, GETDATE())) 
          AS source (ChannelKey, PlaylistId, LastPageToken, LastProcessedDate, LastExecutionDate)
          ON target.ChannelKey = source.ChannelKey AND target.PlaylistId = source.PlaylistId
          WHEN MATCHED THEN
              UPDATE SET 
                  LastPageToken = source.LastPageToken,
                  LastProcessedDate = source.LastProcessedDate,
                  LastExecutionDate = source.LastExecutionDate
          WHEN NOT MATCHED THEN
              INSERT (ChannelKey, PlaylistId, LastPageToken, LastProcessedDate, LastExecutionDate)
              VALUES (source.ChannelKey, source.PlaylistId, source.LastPageToken, source.LastProcessedDate, source.LastExecutionDate);",
            conn);

        cmd.Parameters.AddWithValue("@ChannelKey", channelKey);
        cmd.Parameters.AddWithValue("@PlaylistId", playlistId);
        cmd.Parameters.AddWithValue("@LastPageToken", (object)token ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@LastProcessedDate", (object)maxProcessedDate ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync();
    }
}
