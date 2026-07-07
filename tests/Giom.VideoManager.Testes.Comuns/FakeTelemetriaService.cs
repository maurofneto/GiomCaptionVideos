using Giom.VideoManager.Domain.Interfaces;

namespace Giom.VideoManager.Testes.Comuns;

/// <summary>Fake de ITelemetriaService — só acumula tudo que foi registrado, pra assercao direta nos testes.</summary>
public class FakeTelemetriaService : ITelemetriaService
{
    public List<RegistroTelemetria> Registros { get; } = [];

    public Task RegistrarAsync(RegistroTelemetria registro, CancellationToken ct = default)
    {
        Registros.Add(registro);
        return Task.CompletedTask;
    }
}
