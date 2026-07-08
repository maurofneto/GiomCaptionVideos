using Giom.VideoManager.Domain.Interfaces;

namespace Giom.VideoManager.Testes.Comuns;

/// <summary>Fake de IFilaPublisher — só acumula tudo que foi publicado, pra asserção direta nos testes.</summary>
public class FakeFilaPublisher<TMensagem> : IFilaPublisher<TMensagem>
{
    public List<TMensagem> Publicadas { get; } = [];

    public Task PublicarAsync(TMensagem mensagem, CancellationToken ct = default)
    {
        Publicadas.Add(mensagem);
        return Task.CompletedTask;
    }
}
