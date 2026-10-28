using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Enums;
using Giom.VideoManager.Infrastructure.Supabase;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using System.Net;
using Xunit;

namespace Giom.VideoManager.Application.Tests.Pipeline;

/// <summary>
/// O ChannelProcessor grava cada vídeo visitado com o mesmo UpsertAsync do pipeline (#184). O upsert do PostgREST só
/// altera as colunas presentes no corpo, então um vídeo que acabou de vir do YouTube não pode levar nada do pipeline.
/// </summary>
public class SupabaseVideoRepositoryUpsertTests
{
    private sealed class Captura : HttpMessageHandler
    {
        public List<string> Corpos { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Corpos.Add(request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        }
    }

    private sealed class Fabrica(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler) { BaseAddress = new Uri("https://exemplo.supabase.co") };
    }

    private static (SupabaseVideoRepository Repo, Captura Captura) Criar()
    {
        var captura = new Captura();
        var repo = new SupabaseVideoRepository(
            Microsoft.Extensions.Options.Options.Create(new SupabaseOptions { Url = "https://exemplo.supabase.co" }),
            new Fabrica(captura), NullLogger<SupabaseVideoRepository>.Instance);
        return (repo, captura);
    }

    [Fact]
    public async Task UpsertAsync_VideoVindoDoYouTube_GravaMetadadosRelevanteEStatus_SemProcessedAt()
    {
        var (repo, captura) = Criar();
        var video = new Video
        {
            Id = "v1", Title = "GIOM | culto", ChannelId = "UCxxxxx", PrivacyStatus = "public", Length = "01:30:00",
            Relevante = true, Status = VideoStatus.Active,
            UploadedAt = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc)
        };

        await repo.UpsertAsync(video);

        var corpo = JObject.Parse(Assert.Single(captura.Corpos));
        Assert.False(corpo.ContainsKey("processed_at"));
        Assert.Equal("GIOM | culto", (string?)corpo["title"]);
        Assert.True((bool)corpo["relevante"]!);
        Assert.Equal("active", (string?)corpo["status"]);
    }

    [Fact]
    public async Task UpsertAsync_VideoSoComIdEStatus_NaoLevaTituloNemOutrosTextosVazios()
    {
        var (repo, captura) = Criar();

        await repo.UpsertAsync(new Video { Id = "v2", Status = VideoStatus.Removed });

        var corpo = JObject.Parse(Assert.Single(captura.Corpos));
        Assert.Equal("removed", (string?)corpo["status"]);
        Assert.False(corpo.ContainsKey("title"));
        Assert.False(corpo.ContainsKey("description"));
        Assert.False(corpo.ContainsKey("processed_at"));
        Assert.False(corpo.ContainsKey("relevante"));
        Assert.False(corpo.ContainsKey("type"));
        Assert.False(corpo.ContainsKey("transmissao"));
    }
}
