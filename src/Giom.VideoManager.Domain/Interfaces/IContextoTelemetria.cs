namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>
/// Canal opcional para uma implementação (ClaudeResumoService, ClaudeAgentService, etc.)
/// contribuir metadados ricos de telemetria (provedor, modelo, tokens) sem depender de
/// ITelemetriaService diretamente. Os decorators genéricos (TelemetriaResumoService e afins,
/// em Giom.VideoManager.Infrastructure) leem esses metadados logo após chamar a implementação
/// real e registram a telemetria — se a implementação não chamar Registrar, a telemetria ainda
/// acontece (tempo/sucesso/falha), só sem os campos ricos.
/// </summary>
public interface IContextoTelemetria
{
    void Registrar(string? provedor = null, string? modelo = null, int? tokensEntrada = null, int? tokensSaida = null, decimal? custoEstimadoUsd = null, object? detalhes = null);

    /// <summary>
    /// Abre um escopo de metadados — chamado pelo decorator antes de invocar a implementação
    /// real. Com <paramref name="reaproveitarEscopoExistente"/> = true, se já existir um escopo
    /// aberto por um chamador mais externo (ex.: ChatBotProcessor antes de chamar o
    /// IAgentService, que por sua vez é decorado por TelemetriaAgentService), reaproveita o
    /// mesmo objeto em vez de substituí-lo — só assim o chamador externo enxerga os mesmos
    /// dados que o decorator interno coletou. Decorators sem chamador externo (resumo,
    /// categorização, caption, embedding) devem manter o padrão (false) — sempre abrir um
    /// escopo novo, já que nada acima deles no pipeline de vídeo inicia um escopo próprio.
    /// </summary>
    MetadadosTelemetria IniciarEscopo(bool reaproveitarEscopoExistente = false);
}

/// <summary>Metadados ricos coletados por uma implementação concreta durante uma chamada de IA.</summary>
public class MetadadosTelemetria
{
    public string? Provedor { get; set; }
    public string? Modelo { get; set; }
    public int? TokensEntrada { get; set; }
    public int? TokensSaida { get; set; }
    public decimal? CustoEstimadoUsd { get; set; }
    public object? Detalhes { get; set; }
}
