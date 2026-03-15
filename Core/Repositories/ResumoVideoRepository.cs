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
        using (SqlCommand command = new SqlCommand(
            "UPDATE Video SET PlaylistId = @PlaylistId, ChannelId = @ChannelId, ChannelTitle = @ChannelTitle, Title = @Title, Description = @Description, Length = @Length, PrivacyStatus = @PrivacyStatus, UploadedAt = @UploadedAt, ProcessedAt = GETDATE() " +
            "WHERE Id = @Id", connection))
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

        using (SqlCommand command = new SqlCommand(
            "UPDATE LegendaVideo SET Descricao = @Descricao, Formato = @Formato " +
            "WHERE VideoId = @VideoId", connection))
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("@Descricao", video.Legenda?.Descricao ?? "");
            command.Parameters.AddWithValue("@Formato", video.Legenda?.Formato ?? "");
            command.Parameters.AddWithValue("@VideoId", video.Id);
            command.ExecuteNonQuery();
        }

        using (SqlCommand command = new SqlCommand(
            "UPDATE ResumoVideo SET Descricao = @Descricao " +
            "WHERE VideoId = @VideoId", connection))
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("@Descricao", video.Resumo?.Descricao ?? "");
            command.Parameters.AddWithValue("@VideoId", video.Id);
            command.ExecuteNonQuery();
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
