using Giom.VideoManager.Domain.Interfaces;

namespace Giom.VideoManager.Infrastructure.Observabilidade;

/// <summary>Implementação genérica de ISessionContext via AsyncLocal — sem conhecimento de nenhuma plataforma externa específica.</summary>
public class SessionContext : ISessionContext
{
    private static readonly AsyncLocal<SessaoObservabilidade?> _atual = new();

    public void Definir(SessaoObservabilidade sessao) => _atual.Value = sessao;

    public SessaoObservabilidade? Atual => _atual.Value;
}
