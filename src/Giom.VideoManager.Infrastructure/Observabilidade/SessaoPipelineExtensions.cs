using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;

namespace Giom.VideoManager.Infrastructure.Observabilidade;

/// <summary>
/// Estende a sessão de canal/página/vídeo já aberta pelo ChannelProcessor com o segmento da
/// etapa atual (resumo/categorização), em vez de recomeçar uma sessão isolada por vídeo — é
/// isso que dá a hierarquia canal->página->vídeo->etapa na plataforma de observabilidade
/// externa (Helicone). Se não houver sessão de canal aberta (ex.: chamada avulsa em teste),
/// cai de volta pra uma sessão isolada por vídeo. Compartilhado entre todos os adapters de
/// IA (Anthropic, Grok, OpenAI) — vive no kernel Infrastructure pra evitar referência direta
/// entre projetos Infrastructure.*.
/// </summary>
public static class SessaoPipelineExtensions
{
    public static SessaoObservabilidade ParaEtapa(this SessaoObservabilidade? sessaoBase, string etapa, Video video)
    {
        var propriedades = sessaoBase?.Propriedades is not null
            ? new Dictionary<string, string>(sessaoBase.Propriedades)
            : new Dictionary<string, string>();
        propriedades["Tipo"] = etapa;

        return new SessaoObservabilidade(
            SessionId: sessaoBase?.SessionId ?? video.Id,
            SessionName: sessaoBase?.SessionName ?? video.Title,
            SessionPath: $"{sessaoBase?.SessionPath ?? "/pipeline"}/{etapa}",
            UserId: sessaoBase?.UserId,
            Propriedades: propriedades,
            UsarObservabilidade: sessaoBase?.UsarObservabilidade ?? false);
    }

    /// <summary>
    /// Estampa a decisão de observabilidade (US #106, EtapaProvedorConfig.UsarObservabilidade)
    /// na sessão ambiente ANTES de invocar o provedor concreto — usado só por
    /// FallbackResumoService/FallbackCategorizacaoService, que são os únicos que sabem essa
    /// decisão veio de qual config. O provedor (ClaudeResumoService etc.) chama ParaEtapa em
    /// seguida, que agora preserva esse valor em vez de resetar pro default (false).
    /// </summary>
    public static SessaoObservabilidade ComUsarObservabilidade(this SessaoObservabilidade? sessaoBase, bool usarObservabilidade) =>
        (sessaoBase ?? new SessaoObservabilidade(SessionId: Guid.NewGuid().ToString(), SessionName: "sessao-avulsa", SessionPath: "/pipeline"))
            with
        { UsarObservabilidade = usarObservabilidade };
}
