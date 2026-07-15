namespace Giom.VideoManager.Application.Options;

/// <summary>
/// Configuração da topologia do pipeline orquestrado (Feature #104) — só o PipelineHandler
/// (orquestrador) conhece isso; nenhum worker de etapa sabe o que vem depois de si mesmo.
/// <see cref="Canais"/> mapeia o ChannelId (YouTube) pro nome canônico do canal usado como
/// chave de <see cref="Topologia"/> — usa o Id, não o título exibido, porque título pode ter
/// acento/mudar; Id é estável.
/// <see cref="Topologia"/> é canal → etapa que acabou de terminar → lista de próximas etapas a
/// publicar (lista vazia = terminal, marca ProcessedAt). Fan-out (uma etapa acionando 2+ próximas
/// em paralelo, ex: Transcrição → Resumo + Embedding) é só a lista ter mais de 1 item.
/// </summary>
public class TopologiaOptions
{
    public Dictionary<string, string> Canais { get; init; } = new();
    public Dictionary<string, Dictionary<string, string[]>> Topologia { get; init; } = new();

    /// <summary>
    /// Etapas terminais (lista vazia) da topologia de um canal — bug #111: um canal com fan-out
    /// real (ex: Instituto: transcricao -> [resumo, embedding], onde embedding -> [] E
    /// resumo -> categorizacao -> [] são duas folhas terminais separadas) tem MAIS de uma etapa
    /// terminal, e todas precisam concluir antes do vídeo ser considerado processado. Calculado a
    /// partir do próprio dicionário (chaves ∪ valores cujo GetValueOrDefault dá lista vazia) — não
    /// hardcoded por canal, então continua correto se a topologia mudar no appsettings.
    /// </summary>
    public IReadOnlySet<string> ObterEtapasTerminais(string canal)
    {
        if (!Topologia.TryGetValue(canal, out var porEtapa))
            return new HashSet<string>();

        var todasEtapas = porEtapa.Keys.Concat(porEtapa.Values.SelectMany(v => v)).Distinct();
        return todasEtapas.Where(etapa => porEtapa.GetValueOrDefault(etapa, []).Length == 0).ToHashSet();
    }
}
