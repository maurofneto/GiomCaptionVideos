using Giom.VideoManager.Domain.Entities;

namespace Giom.VideoManager.Application.Handlers;

/// <summary>
/// Papéis por etapa do pipeline orientado a eventos e orquestrado (Feature #33 → Feature #104).
/// Desde a #104, cada implementação concreta (ex: <c>ResumoEtapaHandler</c>) só faz o trabalho da
/// própria etapa e publica <c>EtapaConcluidaMensagem</c> quando termina — não conhece a próxima
/// etapa, não decide fan-out, não marca <c>ProcessedAt</c>. Quem decide "o que vem depois" é o
/// <c>PipelineHandler</c> (orquestrador, único, não mais duplicado por canal), que escuta a fila
/// de eventos de conclusão e resolve a topologia por canal. Por isso não há método "próxima etapa"
/// aqui: a orquestração vive só no orquestrador, nunca no handler de etapa.
/// </summary>
public interface IEtapaTranscricaoHandler
{
    Task ProcessarAsync(Video video, CancellationToken ct = default);
}

public interface IEtapaResumoHandler
{
    Task ProcessarAsync(Video video, CancellationToken ct = default);
}

public interface IEtapaCategorizacaoHandler
{
    Task ProcessarAsync(Video video, CancellationToken ct = default);
}

public interface IEtapaEmbeddingHandler
{
    Task ProcessarAsync(Video video, CancellationToken ct = default);
}
