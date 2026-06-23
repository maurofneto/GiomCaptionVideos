using Core.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Core.Repositories;

public class ResumoVideoRepository
{
    private readonly string ConnectionString;

    public ResumoVideoRepository(IConfiguration configuration)
    {
        ConnectionString = configuration["ConnectionStrings:Default"]
                ?? throw new InvalidOperationException("ConnectionStrings:Default não configurado");
    }

    public async Task UpdateVideoAsync(Core.Models.Video video)
    {
        using (SqlConnection connection = new SqlConnection(ConnectionString))
        {
            connection.Open();
            await UpdateVideoAsync(video, connection);
            connection.Close();
        }
    }

    public async Task UpdateVideoAsync(Core.Models.Video video, SqlConnection connection)
    {
        // Update Video
        using (SqlCommand command = new SqlCommand(
            "UPDATE Video SET PlaylistId = @PlaylistId, ChannelId = @ChannelId, ChannelTitle = @ChannelTitle, Title = @Title, Description = @Description, Length = @Length, PrivacyStatus = @PrivacyStatus, UploadedAt = @UploadedAt, ProcessedAt = GETDATE() " +
            "WHERE Id = @Id", connection))
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("@Id", video.Id);
            command.Parameters.AddWithValue("@PlaylistId", (object?)video.PlaylistId ?? DBNull.Value);
            command.Parameters.AddWithValue("@ChannelId", (object?)video.ChannelId ?? DBNull.Value);
            command.Parameters.AddWithValue("@ChannelTitle", (object?)video.ChannelTitle ?? DBNull.Value);
            command.Parameters.AddWithValue("@Title", (object?)video.Title ?? DBNull.Value);
            command.Parameters.AddWithValue("@Description", (object?)video.Description ?? DBNull.Value);
            command.Parameters.AddWithValue("@Length", (object?)video.Length ?? DBNull.Value);
            command.Parameters.AddWithValue("@PrivacyStatus", (object?)video.PrivacyStatus ?? DBNull.Value);
            command.Parameters.AddWithValue("@UploadedAt", video.UploadedAt);
            await command.ExecuteNonQueryAsync();
        }

        // Upsert LegendaVideo using MERGE
        if (video.Legenda != null)
        {
            using (SqlCommand cmd = new SqlCommand(
                @"MERGE LegendaVideo AS target
                  USING (VALUES (@VideoId, @Descricao, @Formato)) AS source (VideoId, Descricao, Formato)
                  ON target.VideoId = source.VideoId
                  WHEN MATCHED THEN
                      UPDATE SET Descricao = source.Descricao, Formato = source.Formato
                  WHEN NOT MATCHED THEN
                      INSERT (Descricao, Formato, VideoId)
                      VALUES (source.Descricao, source.Formato, source.VideoId)
                  OUTPUT INSERTED.Id;", connection))
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@VideoId", video.Id);
                cmd.Parameters.AddWithValue("@Descricao", (object?)video.Legenda.Descricao ?? "");
                cmd.Parameters.AddWithValue("@Formato", (object?)video.Legenda.Formato ?? "");
                var legendaIdObj = await cmd.ExecuteScalarAsync();
                if (legendaIdObj != null && legendaIdObj != DBNull.Value)
                {
                    video.Legenda.Id = Convert.ToInt32(legendaIdObj);
                    video.Legenda.VideoId = video.Id;
                }
            }
        }

        // Upsert ResumoVideo using MERGE
        if (video.Resumo != null)
        {
            int resumoId = 0;
            using (SqlCommand cmd = new SqlCommand(
                @"MERGE ResumoVideo AS target
                  USING (VALUES (@VideoId, @Descricao)) AS source (VideoId, Descricao)
                  ON target.VideoId = source.VideoId
                  WHEN MATCHED THEN
                      UPDATE SET Descricao = source.Descricao
                  WHEN NOT MATCHED THEN
                      INSERT (Descricao, VideoId)
                      VALUES (source.Descricao, source.VideoId)
                  OUTPUT INSERTED.Id;", connection))
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@VideoId", video.Id);
                cmd.Parameters.AddWithValue("@Descricao", (object?)video.Resumo.Descricao ?? "");
                var resumoIdObj = await cmd.ExecuteScalarAsync();
                if (resumoIdObj != null && resumoIdObj != DBNull.Value)
                {
                    resumoId = Convert.ToInt32(resumoIdObj);
                    video.Resumo.Id = resumoId;
                    video.Resumo.VideoId = video.Id;
                }
            }

            // Replace chapters: delete existing then insert provided ones
            if (resumoId > 0)
            {
                using (SqlCommand delCap = new SqlCommand("DELETE FROM CapituloResumoVideo WHERE ResumoVideoId = @ResumoVideoId", connection))
                {
                    delCap.Parameters.Clear();
                    delCap.Parameters.AddWithValue("@ResumoVideoId", resumoId);
                    await delCap.ExecuteNonQueryAsync();
                }

                if (video.Resumo.Capitulos != null)
                {
                    using (SqlCommand insertCap = new SqlCommand(
                        "INSERT INTO CapituloResumoVideo (Inicio, Resumo, ResumoVideoId) OUTPUT INSERTED.Id VALUES (@Inicio, @Resumo, @ResumoVideoId)", connection))
                    {
                        foreach (var capitulo in video.Resumo.Capitulos)
                        {
                            capitulo.ResumoVideoId = resumoId;
                            insertCap.Parameters.Clear();
                            insertCap.Parameters.AddWithValue("@Inicio", capitulo.Inicio);
                            insertCap.Parameters.AddWithValue("@Resumo", (object?)capitulo.Resumo ?? "");
                            insertCap.Parameters.AddWithValue("@ResumoVideoId", capitulo.ResumoVideoId);
                            var capIdObj = await insertCap.ExecuteScalarAsync();
                            if (capIdObj != null && capIdObj != DBNull.Value)
                            {
                                capitulo.Id = Convert.ToInt32(capIdObj);
                            }
                        }
                    }
                }
            }
        }
    }

    public async Task SaveVideoAsync(Core.Models.Video video)
    {
        using (SqlConnection connection = new SqlConnection(ConnectionString))
        {
            connection.Open();

            await SaveVideoAsync(video, connection);

            connection.Close();
        }
    }

    public async Task SaveVideoAsync(Core.Models.Video video, SqlConnection connection)
    {
        using (SqlCommand command = new SqlCommand("" +
            "INSERT INTO Video (Id, PlaylistId, ChannelId, ChannelTitle, Title, Description, Length, PrivacyStatus, UploadedAt) " +
            "VALUES (@Id, @PlaylistId, @ChannelId, @ChannelTitle, @Title, @Description, @Length, @PrivacyStatus, @UploadedAt)", connection))
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("@Id", video.Id);
            command.Parameters.AddWithValue("@PlaylistId", video.PlaylistId);
            command.Parameters.AddWithValue("@ChannelId", video.ChannelId);
            command.Parameters.AddWithValue("@ChannelTitle", video.ChannelTitle);
            command.Parameters.AddWithValue("@Title", video.Title);
            command.Parameters.AddWithValue("@Description", video.Description);
            command.Parameters.AddWithValue("@Length", video.Length);
            command.Parameters.AddWithValue("@PrivacyStatus", video.PrivacyStatus);
            command.Parameters.AddWithValue("@UploadedAt", video.UploadedAt);
            command.ExecuteNonQuery();
        }

        var legenda = video.Legenda;
        if (legenda != null)
        {
            using (SqlCommand legendaCommand = new SqlCommand("INSERT INTO LegendaVideo (Descricao, Formato, VideoId) VALUES (@Descricao, @Formato, @VideoId)", connection))
            {
                legendaCommand.Parameters.Clear();
                //legendaCommand.Parameters.AddWithValue("@Id", legenda.Id);
                legendaCommand.Parameters.AddWithValue("@Descricao", legenda.Descricao);
                legendaCommand.Parameters.AddWithValue("@Formato", legenda.Formato);
                legendaCommand.Parameters.AddWithValue("@VideoId", legenda.VideoId);
                legendaCommand.ExecuteNonQuery();

                legenda.Id = (int)new SqlCommand("SELECT MAX(Id) FROM LegendaVideo", connection).ExecuteScalar();
            }
        }

        var resumo = video.Resumo;
        if (resumo != null)
        {
            using (SqlCommand resumoCommand = new SqlCommand("INSERT INTO ResumoVideo (Descricao, VideoId) VALUES (@Descricao, @VideoId)", connection))
            {
                resumoCommand.Parameters.Clear();
                //resumoCommand.Parameters.AddWithValue("@Id", resumo.Id);
                resumoCommand.Parameters.AddWithValue("@Descricao", resumo.Descricao);
                resumoCommand.Parameters.AddWithValue("@VideoId", resumo.VideoId);
                resumoCommand.ExecuteNonQuery();

                resumo.Id = (int)new SqlCommand("SELECT MAX(Id) FROM ResumoVideo", connection).ExecuteScalar();

                using (SqlCommand capituloCommand = new SqlCommand("INSERT INTO CapituloResumoVideo (Inicio, Resumo, ResumoVideoId) VALUES (@Inicio, @Resumo, @ResumoVideoId)", connection))
                {
                    foreach (var capitulo in resumo.Capitulos)
                    {
                        capitulo.ResumoVideoId = resumo.Id;

                        capituloCommand.Parameters.Clear();
                        //capituloCommand.Parameters.AddWithValue("@Id", capitulo.Id);
                        capituloCommand.Parameters.AddWithValue("@Inicio", capitulo.Inicio);
                        capituloCommand.Parameters.AddWithValue("@Resumo", capitulo.Resumo);
                        capituloCommand.Parameters.AddWithValue("@ResumoVideoId", capitulo.ResumoVideoId);
                        capituloCommand.ExecuteNonQuery();

                        capitulo.Id = (int)new SqlCommand("SELECT MAX(Id) FROM CapituloResumoVideo", connection).ExecuteScalar();
                    }
                }
            }
        }
    }

    public async Task<Core.Models.Video> GetVideoAsync(string videoId)
    {
        using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        using var cmd = new SqlCommand(
            @"SELECT v.*, 
                  l.Id as LegendaId, l.Descricao AS LegendaDesc, l.Formato AS LegendaFormato, 
                  r.Id as ResumoId, r.Descricao AS ResumoDesc
                  FROM Video v
                  LEFT JOIN LegendaVideo l ON l.VideoId = v.Id
                  LEFT JOIN ResumoVideo r ON r.VideoId = v.Id
                  WHERE v.Id = @Id", conn);

        cmd.Parameters.AddWithValue("@Id", videoId);

        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        var video = new Core.Models.Video
        {
            Id = reader["Id"].ToString(),
            PlaylistId = reader["PlaylistId"].ToString(),
            ChannelId = reader["ChannelId"].ToString(),
            ChannelTitle = reader["ChannelTitle"].ToString(),
            Title = reader["Title"].ToString(),
            Description = reader["Description"].ToString(),
            Length = reader["Length"].ToString(),
            PrivacyStatus = reader["PrivacyStatus"].ToString(),
            UploadedAt = (DateTime)reader["UploadedAt"],
            Legenda = reader["LegendaDesc"] != DBNull.Value
                ? new Core.Models.LegendaVideo { Id = Convert.ToInt32(reader["LegendaId"]), Descricao = reader["LegendaDesc"].ToString(), Formato = reader["LegendaFormato"].ToString(), VideoId = videoId }
                : null,
            Resumo = reader["ResumoDesc"] != DBNull.Value
                ? new Core.Models.ResumoVideo { Id = Convert.ToInt32(reader["ResumoId"]), Descricao = reader["ResumoDesc"].ToString(), VideoId = videoId }
                : null
        };

        return video;
    }
}
