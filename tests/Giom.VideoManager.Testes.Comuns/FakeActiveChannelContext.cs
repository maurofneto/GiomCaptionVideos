using Giom.VideoManager.Domain.Interfaces;

namespace Giom.VideoManager.Testes.Comuns;

/// <summary>Fake de IActiveChannelContext — guarda todos os canais definidos (não só o último) pra permitir assertar a sequência entre canais numa mesma run.</summary>
public class FakeActiveChannelContext : IActiveChannelContext
{
    public List<string> Definidos { get; } = [];

    public void Definir(string channelId) => Definidos.Add(channelId);

    public string? Atual => Definidos.LastOrDefault();
}
