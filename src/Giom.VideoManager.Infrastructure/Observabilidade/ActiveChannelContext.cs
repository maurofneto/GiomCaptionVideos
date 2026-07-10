using Giom.VideoManager.Domain.Interfaces;

namespace Giom.VideoManager.Infrastructure.Observabilidade;

/// <summary>Implementação via AsyncLocal — mesmo padrão de SessionContext.</summary>
public class ActiveChannelContext : IActiveChannelContext
{
    private static readonly AsyncLocal<string?> _atual = new();

    public void Definir(string channelId) => _atual.Value = channelId;

    public string? Atual => _atual.Value;
}
