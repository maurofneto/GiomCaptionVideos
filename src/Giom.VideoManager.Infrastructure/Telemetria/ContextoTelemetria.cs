using Giom.VideoManager.Domain.Interfaces;

namespace Giom.VideoManager.Infrastructure.Telemetria;

/// <summary>
/// Implementação de IContextoTelemetria via AsyncLocal — cada chamada assíncrona (ex.: um
/// GerarResumoAsync) tem seu próprio slot, então chamadas concorrentes não se pisam.
/// O armazenamento é estático de propósito: os decorators genéricos (TelemetriaResumoService
/// e afins) consomem via Consumir(), que não faz parte de IContextoTelemetria — só quem
/// decora precisa ler, quem produz (ClaudeResumoService etc.) só precisa escrever.
/// </summary>
public class ContextoTelemetria : IContextoTelemetria
{
    private static readonly AsyncLocal<MetadadosTelemetria?> _atual = new();

    public void Registrar(string? provedor = null, string? modelo = null, int? tokensEntrada = null, int? tokensSaida = null, object? detalhes = null)
        => _atual.Value = new MetadadosTelemetria(provedor, modelo, tokensEntrada, tokensSaida, detalhes);

    /// <summary>Lê e limpa os metadados contribuídos na chamada atual.</summary>
    public MetadadosTelemetria? Consumir()
    {
        var valor = _atual.Value;
        _atual.Value = null;
        return valor;
    }
}

public record MetadadosTelemetria(string? Provedor, string? Modelo, int? TokensEntrada, int? TokensSaida, object? Detalhes);
