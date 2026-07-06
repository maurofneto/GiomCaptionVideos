using System.Diagnostics;
using Giom.VideoManager.Domain.Interfaces;

namespace Giom.VideoManager.Infrastructure.Telemetria;

/// <summary>Decorator genérico — ver TelemetriaResumoService para a explicação do padrão.</summary>
public class TelemetriaAgentService(IAgentService inner, ITelemetriaService telemetria, ContextoTelemetria contextoTelemetria) : IAgentService
{
    public async Task<string> ResponderAsync(string chatId, string pergunta, string contexto, IReadOnlyList<TurnoConversa> historico, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var meta = contextoTelemetria.IniciarEscopo();
        try
        {
            var resposta = await inner.ResponderAsync(chatId, pergunta, contexto, historico, ct);
            await telemetria.RegistrarAsync(new RegistroTelemetria(
                Tipo: "bot_resposta",
                EntidadeId: chatId,
                TempoMs: stopwatch.ElapsedMilliseconds,
                Sucesso: true,
                Provedor: meta.Provedor,
                Modelo: meta.Modelo,
                TokensEntrada: meta.TokensEntrada,
                TokensSaida: meta.TokensSaida,
                Detalhes: meta.Detalhes
            ), ct);
            return resposta;
        }
        catch
        {
            await telemetria.RegistrarAsync(new RegistroTelemetria(
                Tipo: "bot_resposta",
                EntidadeId: chatId,
                TempoMs: stopwatch.ElapsedMilliseconds,
                Sucesso: false,
                Provedor: meta.Provedor,
                Modelo: meta.Modelo
            ), ct);
            throw;
        }
    }
}
