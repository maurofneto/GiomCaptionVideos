using System.Collections.Concurrent;

namespace Giom.VideoManager.ObservabilidadeTeste;

/// <summary>
/// Atribui, de forma determinística, um cenário de falha a cada combinação {etapa}:{video.Id}
/// (não hash/aleatório — isso não garantia cobertura confiável dos 4 cenários). A chave DEVE
/// ser video.Id (único por mensagem desta run, estável entre redeliveries do SQS) — usar
/// idOrigem colapsa o cenário entre vídeos quando o pool de teste tem dados repetidos.
/// </summary>
public static class InjecaoDeFalha
{
    private static readonly Resultado[] Ciclo =
    [
        Resultado.Sucesso,
        Resultado.FalhaTransitoriaUmaVez,
        Resultado.RateLimitSempre,
        Resultado.FalhaTransitoriaSempre,
    ];

    private static readonly ConcurrentDictionary<string, int> ContadorPorEtapa = new();
    private static readonly ConcurrentDictionary<string, Resultado> CenarioPorVideo = new();
    private static readonly ConcurrentDictionary<string, int> Tentativas = new();

    public enum Resultado { Sucesso, FalhaTransitoriaUmaVez, FalhaTransitoriaSempre, RateLimitSempre }

    public static Resultado Avaliar(string idOrigem, string etapa)
    {
        var chave = $"{etapa}:{idOrigem}";
        var cenario = CenarioPorVideo.GetOrAdd(chave, _ =>
        {
            var ordinal = ContadorPorEtapa.AddOrUpdate(etapa, 0, (_, n) => n + 1);
            return Ciclo[ordinal % Ciclo.Length];
        });

        if (cenario != Resultado.FalhaTransitoriaUmaVez) return cenario;

        var tentativa = Tentativas.AddOrUpdate(chave, 1, (_, n) => n + 1);
        return tentativa == 1 ? cenario : Resultado.Sucesso;
    }
}
