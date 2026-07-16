using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Infrastructure.Resiliencia;

/// <summary>
/// Roda IndexarAsync em TODOS os provedores configurados (Feature #115), um após o outro — ao
/// contrário de FallbackEmbeddingService (busca), aqui uma falha de UM provedor não impede os
/// outros de indexarem: cada provedor grava seus próprios chunks (Origem diferente), então o
/// corpus fica disponível pra qualquer um deles servir de fallback de busca de verdade depois.
/// Único implementador de IVideoIndexingService — interfaces separadas (ISP) pra
/// EmbeddingEtapaHandler/InstitutoVideoHandler nunca precisarem saber que "buscar" existe.
/// </summary>
public sealed class PipelineEmbeddingService(
    IReadOnlyList<(string Nome, IEmbeddingService Servico)> cadeia,
    ILogger<PipelineEmbeddingService> logger) : IVideoIndexingService
{
    public async Task IndexarAsync(Video video, CancellationToken ct = default)
    {
        foreach (var (nome, servico) in cadeia)
        {
            try
            {
                await servico.IndexarAsync(video, ct);
            }
            catch (Exception ex)
            {
                // Log (não throw) é deliberado — segue pros próximos provedores mesmo com falha
                // de um deles (diferente de FallbackHandler, que existe pra PARAR no primeiro
                // sucesso).
                logger.LogError(ex, "Pipeline de indexação: provedor {Provider} falhou pra {VideoId} — seguindo com os demais", nome, video.Id);
            }
        }
    }
}
