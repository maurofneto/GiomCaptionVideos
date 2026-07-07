using Giom.VideoManager.Domain.Interfaces;

namespace Giom.VideoManager.Infrastructure.Telemetria;

/// <summary>
/// Implementação de IContextoTelemetria via AsyncLocal — cada chamada assíncrona (ex.: um
/// GerarResumoAsync) tem seu próprio slot, então chamadas concorrentes não se pisam.
///
/// Importante: AsyncLocal só propaga valores pra baixo (de quem chama pra quem é chamado) —
/// uma REATRIBUIÇÃO feita numa chamada mais funda na pilha (ex.: dentro de ClaudeResumoService,
/// chamado via await por um decorator) NÃO fica visível de volta pro chamador depois do await
/// retornar (é o ExecutionContext seguindo semântica de cópia, não de referência compartilhada
/// bidirecional). Por isso o valor carregado no AsyncLocal é um objeto MUTÁVEL: o decorator cria
/// o objeto antes de chamar a implementação real (IniciarEscopo), guarda a referência numa
/// variável local, e a implementação real só MUTA os campos desse mesmo objeto (Registrar) — a
/// mutação é visível de volta porque o decorator já segura a mesma referência, sem precisar ler
/// o AsyncLocal de novo depois do await.
/// </summary>
public class ContextoTelemetria : IContextoTelemetria
{
    private static readonly AsyncLocal<MetadadosTelemetria?> _atual = new();

    public MetadadosTelemetria IniciarEscopo(bool reaproveitarEscopoExistente = false)
    {
        if (reaproveitarEscopoExistente && _atual.Value is { } existente) return existente;

        var novo = new MetadadosTelemetria();
        _atual.Value = novo;
        return novo;
    }

    public void Registrar(string? provedor = null, string? modelo = null, int? tokensEntrada = null, int? tokensSaida = null, decimal? custoEstimadoUsd = null, object? detalhes = null)
    {
        var atual = _atual.Value;
        if (atual == null) return; // chamado fora de um escopo iniciado pelo decorator — no-op

        atual.Provedor = provedor;
        atual.Modelo = modelo;
        atual.TokensEntrada = tokensEntrada;
        atual.TokensSaida = tokensSaida;
        atual.CustoEstimadoUsd = custoEstimadoUsd;
        atual.Detalhes = detalhes;
    }
}
