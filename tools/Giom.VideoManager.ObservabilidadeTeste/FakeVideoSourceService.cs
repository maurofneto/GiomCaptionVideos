using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Enums;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;

namespace Giom.VideoManager.ObservabilidadeTeste;

/// <summary>
/// Substitui a busca real no YouTube por uma playlist inventada, sempre "nova" (Id/UploadedAt
/// gerados a cada execução) — assim o filtro de data do ChannelProcessor nunca marca esses
/// vídeos fake como "já processado" e cada run reprocessa tudo de novo, sem precisar zerar
/// progresso manualmente. Títulos/transcrições propositalmente acentuados, pra exercitar o
/// mesmo caminho de código que já quebrou em produção (ParaAscii do HeliconeHeadersHandler).
/// </summary>
public class FakeVideoSourceService : IVideoSourceService
{
    private static readonly (string Titulo, string Transcricao)[] Aulas =
    [
        ("Como Interpretar a Bíblia em seu Contexto Histórico",
         "Hoje vamos falar sobre hermenêutica bíblica e a importância do contexto histórico-cultural " +
         "na interpretação das Escrituras. É fundamental entendermos que cada texto foi escrito para " +
         "uma audiência específica, num momento específico da história de Israel e da Igreja primitiva."),
        ("A Doutrina da Graça segundo João Calvino",
         "Nesta aula estudamos a soberania de Deus na salvação e a doutrina da eleição, conforme " +
         "desenvolvida pelos reformadores. A graça não é algo que merecemos — é dom imerecido de Deus " +
         "concedido segundo Seu beneplácito, não segundo nossas obras."),
        ("Escatologia: O que Significa a Segunda Vinda?",
         "Vamos examinar as principais correntes escatológicas — pré-milenismo, pós-milenismo e " +
         "amilenismo — e como cada uma interpreta as profecias sobre o retorno de Cristo e o " +
         "estabelecimento do Seu Reino eterno."),
    ];

    public Task<string> GetUploadPlaylistIdAsync(ChannelConfig config, CancellationToken ct = default)
        => Task.FromResult("fake-playlist-observabilidade");

    public Task<PlaylistPage> GetPlaylistPageAsync(string playlistId, int maxResults, string? pageToken, CancellationToken ct = default)
    {
        // pageToken sempre null (uma página só) — o teste é sobre a etapa de vídeo/resumo/
        // categorização, não sobre paginação (isso já tem cobertura em ChannelProcessorTests).
        var agora = DateTime.UtcNow;

        var videos = Aulas.Select((aula, i) =>
        {
            var videoId = $"fake-{Guid.NewGuid():N}"[..12];
            return new Video
            {
                Id = videoId,
                Title = aula.Titulo,
                Description = "Vídeo fake gerado pelo Giom.VideoManager.ObservabilidadeTeste — não existe no YouTube.",
                ChannelId = "fake-instituto-observabilidade",
                ChannelTitle = "Instituto Giom (Teste de Observabilidade)",
                Type = VideoType.Live,
                PrivacyStatus = "unlisted",
                UploadedAt = agora.AddMinutes(-i),
                Transcricao = new Transcricao
                {
                    VideoId = videoId,
                    Conteudo = aula.Transcricao,
                    ConteudoLimpo = aula.Transcricao,
                    Formato = "txt",
                    Origem = "Fake",
                    GeradoEm = agora
                }
            };
        }).ToList();

        return Task.FromResult(new PlaylistPage(videos, NextPageToken: null));
    }
}
