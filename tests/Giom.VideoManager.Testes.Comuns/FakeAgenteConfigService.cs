using Giom.VideoManager.Domain.Interfaces;

namespace Giom.VideoManager.Testes.Comuns;

/// <summary>agente_config em memória: devolve a mesma instrução de concisão (vazia por padrão) pra qualquer provedor.</summary>
public sealed class FakeAgenteConfigService(string instrucaoConcisao = "") : IAgenteConfigService
{
    public Task<string> ObterInstrucaoConcisaoAsync(string provedor, CancellationToken ct = default) =>
        Task.FromResult(instrucaoConcisao);
}
