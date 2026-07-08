using Giom.VideoManager.Domain.Entities;

namespace Giom.VideoManager.Application.Handlers;

/// <summary>
/// Papéis por etapa do pipeline orientado a eventos (Feature #33, US #41/#42/#43) — cada canal
/// implementa só as etapas que usa (Ministério não categoriza nem indexa embedding, por exemplo),
/// sem precisar de método vazio à toa (ISP). Quem decide o que roda depois de cada etapa é o
/// próprio handler concreto (InstitutoPipelineHandler/MinisterioPipelineHandler) — ele publica a
/// próxima mensagem sabendo o ProcessingOptions do canal, por isso não há um método "próxima
/// etapa" aqui: a orquestração vive no handler, não no consumer genérico que chama ProcessarAsync.
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
