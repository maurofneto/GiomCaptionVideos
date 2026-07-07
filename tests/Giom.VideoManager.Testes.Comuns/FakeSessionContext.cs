using Giom.VideoManager.Domain.Interfaces;

namespace Giom.VideoManager.Testes.Comuns;

/// <summary>Fake de ISessionContext — guarda todas as sessões definidas (não só a última) pra permitir assertar a sequência canal/página/vídeo/etapa.</summary>
public class FakeSessionContext : ISessionContext
{
    public List<SessaoObservabilidade> Definidas { get; } = [];

    public void Definir(SessaoObservabilidade sessao) => Definidas.Add(sessao);

    public SessaoObservabilidade? Atual => Definidas.LastOrDefault();
}
