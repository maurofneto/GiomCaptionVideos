using System.Diagnostics;
using Giom.VideoManager.Domain.Interfaces;

namespace Giom.VideoManager.Infrastructure.Telemetria;

/// <summary>Decorator genérico — ver TelemetriaResumoService para a explicação do padrão.</summary>
public class TelemetriaAgentService(IAgentService inner, ITelemetriaService telemetria, ContextoTelemetria contextoTelemetria) : IAgentService
{
    public async Task<string> ResponderAsync(string chatId, TurnoConversa turnoAtual, string contexto, IReadOnlyList<TurnoConversa> historico, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        // reaproveita o escopo do ChatBotProcessor se ele já abriu um (pra expor tokens/custo de
        // volta pro chamador, ex.: mensagem de debug em Dev) — senão abre um novo, como sempre.
        var meta = contextoTelemetria.IniciarEscopo(reaproveitarEscopoExistente: true);

        // turnoAtual.Id já existe nesse ponto (quem orquestra grava a pergunta antes de chamar
        // o agente) — identifica a pergunta/resposta específica, não só o chat.
        var entidadeId = turnoAtual.Id?.ToString() ?? chatId;

        try
        {
            var resposta = await inner.ResponderAsync(chatId, turnoAtual, contexto, historico, ct);
            await telemetria.RegistrarAsync(new RegistroTelemetria(
                Tipo: "bot_resposta",
                EntidadeId: entidadeId,
                TempoMs: stopwatch.ElapsedMilliseconds,
                Sucesso: true,
                Provedor: meta.Provedor,
                Modelo: meta.Modelo,
                TokensEntrada: meta.TokensEntrada,
                TokensSaida: meta.TokensSaida,
                CustoEstimadoUsd: meta.CustoEstimadoUsd,
                Detalhes: meta.Detalhes
            ), ct);
            return resposta;
        }
        catch
        {
            await telemetria.RegistrarAsync(new RegistroTelemetria(
                Tipo: "bot_resposta",
                EntidadeId: entidadeId,
                TempoMs: stopwatch.ElapsedMilliseconds,
                Sucesso: false,
                Provedor: meta.Provedor,
                Modelo: meta.Modelo
            ), ct);
            throw;
        }
    }
}
