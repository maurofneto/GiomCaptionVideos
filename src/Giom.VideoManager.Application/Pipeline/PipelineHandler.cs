using Giom.VideoManager.Application.Options;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Giom.VideoManager.Application.Pipeline;

/// <summary>
/// Orquestrador central do pipeline (Feature #104) — único, não mais duplicado por canal. Escuta
/// eventos de "etapa concluída" e decide o que fazer a seguir a partir da topologia por canal
/// (<see cref="TopologiaOptions"/>): publica na(s) próxima(s) fila(s) de etapa (fan-out, se a
/// lista tiver mais de 1 item) ou marca ProcessedAt se for terminal (lista vazia). Nenhum worker
/// de etapa decide isso sozinho — "quem manda" é só aqui, um lugar só.
/// </summary>
public class PipelineHandler(
    IVideoRepository videoRepository,
    IVideoEtapaTerminalRepository etapaTerminalRepository,
    IOptions<TopologiaOptions> options,
    IServiceProvider serviceProvider,
    ILogger<PipelineHandler> logger)
{
    private readonly TopologiaOptions _options = options.Value;

    public async Task ProcessarAsync(EtapaConcluidaMensagem mensagem, CancellationToken ct)
    {
        var video = mensagem.Video;

        if (!_options.Canais.TryGetValue(video.ChannelId, out var canal))
            throw new InvalidOperationException($"Canal desconhecido pro orquestrador: ChannelId={video.ChannelId} ({video.ChannelTitle}) — falta em Topologia:Canais");

        if (!_options.Topologia.TryGetValue(canal, out var porEtapa))
            throw new InvalidOperationException($"Canal {canal} não tem topologia configurada em Topologia:Topologia");

        // Uma etapa terminal ("etapa": [] no JSON) não sobra nenhuma chave flatten na
        // IConfiguration — o binder nem chega a criar a entrada no dicionário. "Não encontrada"
        // e "lista vazia" são indistinguíveis vindo de appsettings.json, então tratamos as duas
        // como terminal (sem isso, toda etapa final da topologia real lançava aqui).
        var proximasEtapas = porEtapa.GetValueOrDefault(mensagem.Etapa, []);

        if (proximasEtapas.Length == 0)
        {
            // Bug #111: uma topologia com fan-out real (ex: Instituto: transcricao -> [resumo,
            // embedding], onde embedding e resumo->categorizacao são DUAS folhas terminais
            // separadas) só pode marcar ProcessedAt quando TODOS os ramos terminais concluírem —
            // não só o primeiro a chegar aqui. Só entra nesse rastreamento se mensagem.Etapa for
            // uma etapa terminal DE VERDADE (parte do universo conhecido da topologia do canal);
            // uma etapa fora desse universo (ver EtapaSemEntradaNaTopologia_TrataComoTerminal) não
            // tem como ser "esperada" em nenhum conjunto, então continua marcando na hora, como
            // sempre foi.
            var etapasTerminais = _options.ObterEtapasTerminais(canal);
            if (etapasTerminais.Contains(mensagem.Etapa))
            {
                var concluidas = await etapaTerminalRepository.RegistrarConclusaoAsync(video.Id, mensagem.Etapa, ct);
                if (!etapasTerminais.IsSubsetOf(concluidas))
                {
                    logger.LogInformation("{Canal} — ramo terminal {Etapa} concluído, aguardando outros ramos ({Concluidas}/{Total}): {Title}",
                        canal, mensagem.Etapa, concluidas.Count, etapasTerminais.Count, video.Title);
                    return;
                }
            }

            video.ProcessedAt = DateTime.UtcNow;
            await videoRepository.UpsertAsync(video, ct);
            logger.LogInformation("{Canal} — pipeline concluído (terminal em {Etapa}): {Title}", canal, mensagem.Etapa, video.Title);
            return;
        }

        foreach (var proximaEtapa in proximasEtapas)
        {
            var publisher = serviceProvider.GetRequiredKeyedService<IFilaPublisher<VideoEtapaMensagem>>(proximaEtapa);
            await publisher.PublicarAsync(new VideoEtapaMensagem(video, mensagem.Sessao, mensagem.TraceParent), ct);
            logger.LogInformation("{Canal} — {EtapaAnterior} concluída, publicando {ProximaEtapa}: {Title}", canal, mensagem.Etapa, proximaEtapa, video.Title);
        }
    }
}
