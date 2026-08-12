using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Exceptions;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Infrastructure.Resiliencia;

/// <summary>
/// Roda IndexarAsync em TODOS os provedores configurados pro canal do vídeo (Feature #115,
/// cadeia resolvida via IEtapaProvedorConfigService — US #129, mesmo mecanismo de
/// FallbackResumoService/US #106) — ao contrário de FallbackEmbeddingService (busca), aqui uma
/// falha de UM provedor não impede os outros de indexarem: cada provedor grava seus próprios
/// chunks (Origem diferente), então o corpus fica disponível pra qualquer um deles servir de
/// fallback de busca de verdade depois. Único implementador de IVideoIndexingService —
/// interfaces separadas (ISP) pra EmbeddingEtapaHandler/InstitutoVideoHandler nunca precisarem
/// saber que "buscar" existe.
/// </summary>
public sealed class PipelineEmbeddingService(
    IEtapaProvedorConfigService configService,
    IServiceProvider serviceProvider,
    ILogger<PipelineEmbeddingService> logger) : IVideoIndexingService
{
    public async Task IndexarAsync(Video video, CancellationToken ct = default)
    {
        var cadeia = await configService.ObterCadeiaAsync(video.ChannelId, "embedding", ct);

        // Cadeia vazia (canal sem linha de etapa_provedor_config pra "embedding" — migration não
        // aplicada, ou config apagada por engano) não pode virar um "foreach" que não roda nada
        // e ainda assim reporta sucesso pro orquestrador (Bug #134 — mesma classe de falha
        // silenciosa que motivou o resto do fallback lançar exceção em vez de retornar vazio).
        if (cadeia.Count == 0)
            throw new NenhumResultadoEncontradoException("PipelineEmbeddingService", $"nenhum provedor configurado em etapa_provedor_config pro canal {video.ChannelId}, etapa 'embedding'");

        foreach (var config in cadeia)
        {
            try
            {
                var servico = serviceProvider.GetRequiredKeyedService<IEmbeddingService>(config.Provedor);
                await servico.IndexarAsync(video, ct);
            }
            catch (Exception ex)
            {
                // Log (não throw) é deliberado — segue pros próximos provedores mesmo com falha
                // de um deles (diferente de FallbackHandler, que existe pra PARAR no primeiro
                // sucesso).
                logger.LogError(ex, "Pipeline de indexação: provedor {Provider} falhou pra {VideoId} — seguindo com os demais", config.Provedor, video.Id);
            }
        }
    }
}
