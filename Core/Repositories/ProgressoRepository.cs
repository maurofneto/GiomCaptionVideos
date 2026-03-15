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

    public async Task SaveLastPageTokenAsync(string channelKey, string playlistId, string token)
    {
        using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        using var cmd = new SqlCommand(
            @"MERGE ProcessamentoProgresso AS target
          USING (VALUES (@ChannelKey, @PlaylistId, @LastPageToken, GETDATE())) 
          AS source (ChannelKey, PlaylistId, LastPageToken, UltimaExecucao)
          ON target.ChannelKey = source.ChannelKey AND target.PlaylistId = source.PlaylistId
          WHEN MATCHED THEN
              UPDATE SET 
                  LastPageToken = source.LastPageToken,
                  UltimaExecucao = source.UltimaExecucao
          WHEN NOT MATCHED THEN
              INSERT (ChannelKey, PlaylistId, LastPageToken, UltimaExecucao)
              VALUES (source.ChannelKey, source.PlaylistId, source.LastPageToken, source.UltimaExecucao);",
            conn);

        cmd.Parameters.AddWithValue("@ChannelKey", channelKey);
        cmd.Parameters.AddWithValue("@PlaylistId", playlistId);
        cmd.Parameters.AddWithValue("@LastPageToken", (object)token ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync();
    }
}
