namespace Giom.VideoManager.Domain.ValueObjects;

/// <summary>
/// Topologia do pipeline de UM canal (tabela etapa_topologia): etapa que acabou de terminar →
/// próximas etapas a publicar. Lista vazia = etapa terminal; fan-out (ex: transcrição → resumo +
/// embedding) é só a lista ter mais de 1 item. Etapa fora do dicionário também é tratada como
/// terminal (não há o que publicar depois dela).
/// </summary>
public sealed class TopologiaCanal(IReadOnlyDictionary<string, string[]> porEtapa)
{
    public IReadOnlyDictionary<string, string[]> PorEtapa { get; } = porEtapa;

    public string[] ObterProximas(string etapa) => PorEtapa.GetValueOrDefault(etapa, []);

    /// <summary>
    /// Etapas terminais (lista vazia) — bug #111: um canal com fan-out real (ex: Instituto:
    /// transcricao -> [resumo, embedding], onde embedding -> [] E resumo -> categorizacao -> [] são
    /// duas folhas terminais separadas) tem MAIS de uma etapa terminal, e todas precisam concluir
    /// antes do vídeo ser considerado processado. Calculado a partir do próprio dicionário (chaves ∪
    /// valores cuja lista de próximas é vazia) — não hardcoded por canal.
    /// </summary>
    public IReadOnlySet<string> ObterEtapasTerminais()
    {
        var todasEtapas = PorEtapa.Keys.Concat(PorEtapa.Values.SelectMany(v => v)).Distinct();
        return todasEtapas.Where(etapa => ObterProximas(etapa).Length == 0).ToHashSet();
    }
}
