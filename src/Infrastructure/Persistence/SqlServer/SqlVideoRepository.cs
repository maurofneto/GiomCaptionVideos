using Domain.Entities;
using Domain.Interfaces;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.Persistence.SqlServer;

public class SqlVideoRepository : IVideoRepository
{
    private readonly string _connectionString;

    public SqlVideoRepository(IConfiguration configuration)
    {
        _connectionString = configuration["ConnectionStrings:Default"]
            ?? throw new InvalidOperationException("ConnectionStrings:Default não configurado");
    }

    public async Task<Video?> GetByIdAsync(string videoId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(
            @"SELECT v.*,
                l.Id AS LegendaId, l.Conteudo AS LegendaConteudo, l.Formato AS LegendaFormato, l.Origem AS LegendaOrigem,
                r.Id AS ResumoId, r.Descricao AS ResumoDesc, r.Origem AS ResumoOrigem,
                cat.Id AS CatId, cat.Area AS CatArea, cat.Subcategoria AS CatSub
              FROM Video v
              LEFT JOIN Transcricao l ON l.VideoId = v.Id
              LEFT JOIN Resumo r ON r.VideoId = v.Id
              LEFT JOIN CategoriaTeologica cat ON cat.VideoId = v.Id
              WHERE v.Id = @Id", conn);

        cmd.Parameters.AddWithValue("@Id", videoId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return MapVideo(reader, videoId);
    }

    public async Task SaveAsync(Video video, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = conn.BeginTransaction();

        try
        {
            await InsertVideoAsync(video, conn, tx, ct);
            if (video.Transcricao != null) await InsertTranscricaoAsync(video.Transcricao, conn, tx, ct);
            if (video.Resumo != null) await InsertResumoAsync(video.Resumo, conn, tx, ct);
            foreach (var cat in video.Categorias) await InsertCategoriaAsync(cat, conn, tx, ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task UpdateAsync(Video video, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = conn.BeginTransaction();

        try
        {
            await UpdateVideoAsync(video, conn, tx, ct);
            if (video.Transcricao != null) await UpsertTranscricaoAsync(video.Transcricao, conn, tx, ct);
            if (video.Resumo != null) await UpsertResumoAsync(video.Resumo, conn, tx, ct);
            foreach (var cat in video.Categorias) await UpsertCategoriaAsync(cat, conn, tx, ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private static async Task InsertVideoAsync(Video v, SqlConnection conn, SqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "INSERT INTO Video (Id, PlaylistId, ChannelId, ChannelTitle, Title, Description, Length, PrivacyStatus, UploadedAt) " +
            "VALUES (@Id, @PlaylistId, @ChannelId, @ChannelTitle, @Title, @Description, @Length, @PrivacyStatus, @UploadedAt)",
            conn, tx);

        cmd.Parameters.AddWithValue("@Id", v.Id);
        cmd.Parameters.AddWithValue("@PlaylistId", (object?)v.PlaylistId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ChannelId", (object?)v.ChannelId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ChannelTitle", (object?)v.ChannelTitle ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Title", (object?)v.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Description", (object?)v.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Length", (object?)v.Length ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@PrivacyStatus", (object?)v.PrivacyStatus ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@UploadedAt", (object?)v.UploadedAt ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpdateVideoAsync(Video v, SqlConnection conn, SqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "UPDATE Video SET PlaylistId=@PlaylistId, ChannelId=@ChannelId, ChannelTitle=@ChannelTitle, " +
            "Title=@Title, Description=@Description, Length=@Length, PrivacyStatus=@PrivacyStatus, " +
            "UploadedAt=@UploadedAt, ProcessedAt=GETDATE() WHERE Id=@Id",
            conn, tx);

        cmd.Parameters.AddWithValue("@Id", v.Id);
        cmd.Parameters.AddWithValue("@PlaylistId", (object?)v.PlaylistId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ChannelId", (object?)v.ChannelId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ChannelTitle", (object?)v.ChannelTitle ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Title", (object?)v.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Description", (object?)v.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Length", (object?)v.Length ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@PrivacyStatus", (object?)v.PrivacyStatus ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@UploadedAt", (object?)v.UploadedAt ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertTranscricaoAsync(Transcricao t, SqlConnection conn, SqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "INSERT INTO Transcricao (VideoId, Conteudo, Formato, Origem, GeradoEm) OUTPUT INSERTED.Id " +
            "VALUES (@VideoId, @Conteudo, @Formato, @Origem, @GeradoEm)", conn, tx);
        AddTranscricaoParams(cmd, t);
        t.Id = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task UpsertTranscricaoAsync(Transcricao t, SqlConnection conn, SqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            @"MERGE Transcricao AS target
              USING (VALUES (@VideoId, @Conteudo, @Formato, @Origem, @GeradoEm))
                AS source (VideoId, Conteudo, Formato, Origem, GeradoEm)
              ON target.VideoId = source.VideoId
              WHEN MATCHED THEN UPDATE SET Conteudo=source.Conteudo, Formato=source.Formato, Origem=source.Origem
              WHEN NOT MATCHED THEN INSERT (VideoId, Conteudo, Formato, Origem, GeradoEm)
                VALUES (source.VideoId, source.Conteudo, source.Formato, source.Origem, source.GeradoEm)
              OUTPUT INSERTED.Id;", conn, tx);
        AddTranscricaoParams(cmd, t);
        t.Id = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private static void AddTranscricaoParams(SqlCommand cmd, Transcricao t)
    {
        cmd.Parameters.AddWithValue("@VideoId", t.VideoId);
        cmd.Parameters.AddWithValue("@Conteudo", (object?)t.Conteudo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Formato", (object?)t.Formato ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Origem", (object?)t.Origem ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@GeradoEm", t.GeradoEm);
    }

    private static async Task InsertResumoAsync(Resumo r, SqlConnection conn, SqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "INSERT INTO Resumo (VideoId, Descricao, Origem, GeradoEm) OUTPUT INSERTED.Id " +
            "VALUES (@VideoId, @Descricao, @Origem, @GeradoEm)", conn, tx);
        AddResumoParams(cmd, r);
        r.Id = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        await InsertCapitulosAsync(r, conn, tx, ct);
    }

    private static async Task UpsertResumoAsync(Resumo r, SqlConnection conn, SqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            @"MERGE Resumo AS target
              USING (VALUES (@VideoId, @Descricao, @Origem, @GeradoEm))
                AS source (VideoId, Descricao, Origem, GeradoEm)
              ON target.VideoId = source.VideoId
              WHEN MATCHED THEN UPDATE SET Descricao=source.Descricao, Origem=source.Origem
              WHEN NOT MATCHED THEN INSERT (VideoId, Descricao, Origem, GeradoEm)
                VALUES (source.VideoId, source.Descricao, source.Origem, source.GeradoEm)
              OUTPUT INSERTED.Id;", conn, tx);
        AddResumoParams(cmd, r);
        r.Id = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));

        await using var del = new SqlCommand("DELETE FROM Capitulo WHERE ResumoId=@ResumoId", conn, tx);
        del.Parameters.AddWithValue("@ResumoId", r.Id);
        await del.ExecuteNonQueryAsync(ct);

        await InsertCapitulosAsync(r, conn, tx, ct);
    }

    private static void AddResumoParams(SqlCommand cmd, Resumo r)
    {
        cmd.Parameters.AddWithValue("@VideoId", r.VideoId);
        cmd.Parameters.AddWithValue("@Descricao", (object?)r.Descricao ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Origem", (object?)r.Origem ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@GeradoEm", r.GeradoEm);
    }

    private static async Task InsertCapitulosAsync(Resumo r, SqlConnection conn, SqlTransaction tx, CancellationToken ct)
    {
        if (r.Capitulos == null || r.Capitulos.Length == 0) return;

        await using var cmd = new SqlCommand(
            "INSERT INTO Capitulo (ResumoId, Inicio, Texto) OUTPUT INSERTED.Id VALUES (@ResumoId, @Inicio, @Texto)",
            conn, tx);

        foreach (var cap in r.Capitulos)
        {
            cap.ResumoId = r.Id;
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("@ResumoId", cap.ResumoId);
            cmd.Parameters.AddWithValue("@Inicio", cap.Inicio);
            cmd.Parameters.AddWithValue("@Texto", (object?)cap.Texto ?? DBNull.Value);
            cap.Id = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }
    }

    private static async Task InsertCategoriaAsync(CategoriaTeologica cat, SqlConnection conn, SqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "INSERT INTO CategoriaTeologica (VideoId, Area, Subcategoria, ClassificadoEm) OUTPUT INSERTED.Id " +
            "VALUES (@VideoId, @Area, @Subcategoria, @ClassificadoEm)", conn, tx);
        AddCategoriaParams(cmd, cat);
        cat.Id = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task UpsertCategoriaAsync(CategoriaTeologica cat, SqlConnection conn, SqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            @"MERGE CategoriaTeologica AS target
              USING (VALUES (@VideoId, @Area, @Subcategoria, @ClassificadoEm))
                AS source (VideoId, Area, Subcategoria, ClassificadoEm)
              ON target.VideoId = source.VideoId
              WHEN MATCHED THEN UPDATE SET Area=source.Area, Subcategoria=source.Subcategoria
              WHEN NOT MATCHED THEN INSERT (VideoId, Area, Subcategoria, ClassificadoEm)
                VALUES (source.VideoId, source.Area, source.Subcategoria, source.ClassificadoEm)
              OUTPUT INSERTED.Id;", conn, tx);
        AddCategoriaParams(cmd, cat);
        cat.Id = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private static void AddCategoriaParams(SqlCommand cmd, CategoriaTeologica cat)
    {
        cmd.Parameters.AddWithValue("@VideoId", cat.VideoId);
        cmd.Parameters.AddWithValue("@Area", cat.Area.ToString());
        cmd.Parameters.AddWithValue("@Subcategoria", (object?)cat.Subcategoria ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ClassificadoEm", cat.ClassificadoEm);
    }

    private static Video MapVideo(SqlDataReader reader, string videoId)
    {
        Transcricao? transcricao = null;
        if (reader["LegendaId"] != DBNull.Value)
        {
            transcricao = new Transcricao
            {
                Id = Convert.ToInt32(reader["LegendaId"]),
                VideoId = videoId,
                Conteudo = reader["LegendaConteudo"]?.ToString() ?? "",
                Formato = reader["LegendaFormato"]?.ToString() ?? "",
                Origem = reader["LegendaOrigem"]?.ToString() ?? ""
            };
        }

        Resumo? resumo = null;
        if (reader["ResumoId"] != DBNull.Value)
        {
            resumo = new Resumo
            {
                Id = Convert.ToInt32(reader["ResumoId"]),
                VideoId = videoId,
                Descricao = reader["ResumoDesc"]?.ToString() ?? "",
                Origem = reader["ResumoOrigem"]?.ToString() ?? ""
            };
        }

        var categorias = new List<CategoriaTeologica>();
        if (reader["CatId"] != DBNull.Value &&
            Enum.TryParse<Domain.Enums.AreaTeologica>(reader["CatArea"]?.ToString(), out var area))
        {
            categorias.Add(new CategoriaTeologica
            {
                Id = Convert.ToInt32(reader["CatId"]),
                VideoId = videoId,
                Area = area,
                Subcategoria = reader["CatSub"]?.ToString() ?? "",
                Peso = reader["CatPeso"] == DBNull.Value ? 1.0m : Convert.ToDecimal(reader["CatPeso"])
            });
        }

        return new Video
        {
            Id = reader["Id"].ToString()!,
            PlaylistId = reader["PlaylistId"]?.ToString() ?? "",
            ChannelId = reader["ChannelId"]?.ToString() ?? "",
            ChannelTitle = reader["ChannelTitle"]?.ToString() ?? "",
            Title = reader["Title"]?.ToString() ?? "",
            Description = reader["Description"]?.ToString() ?? "",
            Length = reader["Length"]?.ToString() ?? "",
            PrivacyStatus = reader["PrivacyStatus"]?.ToString() ?? "",
            UploadedAt = reader["UploadedAt"] as DateTime?,
            ProcessedAt = reader["ProcessedAt"] as DateTime?,
            Transcricao = transcricao,
            Resumo = resumo,
            Categorias = categorias
        };
    }
}
