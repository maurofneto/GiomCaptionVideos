using Giom.VideoManager.Domain.Interfaces;

namespace Giom.VideoManager.Testes.Comuns;

/// <summary>
/// Fake de IContextoTelemetria — sem AsyncLocal (não precisa: cada teste roda sua própria
/// instância), mas com a MESMA semântica de Registrar virar no-op fora de um escopo e de
/// IniciarEscopo(reaproveitarEscopoExistente: true) devolver o objeto já aberto (ver
/// ContextoTelemetria real, Giom.VideoManager.Infrastructure.Telemetria).
/// </summary>
public class FakeContextoTelemetria : IContextoTelemetria
{
    private MetadadosTelemetria? _atual;

    public MetadadosTelemetria IniciarEscopo(bool reaproveitarEscopoExistente = false)
    {
        if (reaproveitarEscopoExistente && _atual is not null) return _atual;
        return _atual = new MetadadosTelemetria();
    }

    public void Registrar(string? provedor = null, string? modelo = null, int? tokensEntrada = null, int? tokensSaida = null, decimal? custoEstimadoUsd = null, object? detalhes = null, string? requestIdExterno = null)
    {
        if (_atual is null) return;

        _atual.Provedor = provedor;
        _atual.Modelo = modelo;
        _atual.TokensEntrada = tokensEntrada;
        _atual.TokensSaida = tokensSaida;
        _atual.CustoEstimadoUsd = custoEstimadoUsd;
        _atual.Detalhes = detalhes;
        _atual.RequestIdExterno = requestIdExterno;
    }
}
