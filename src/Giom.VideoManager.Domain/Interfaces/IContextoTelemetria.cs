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
    void Registrar(string? provedor = null, string? modelo = null, int? tokensEntrada = null, int? tokensSaida = null, object? detalhes = null);
}
