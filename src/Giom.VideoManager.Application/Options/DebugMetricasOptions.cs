namespace Giom.VideoManager.Application.Options;

/// <summary>
/// Mensagem de acompanhamento (sem botões de avaliação) enviada após cada resposta do bot,
/// com tokens/tempo/custo/uso da janela de contexto — só habilitado em Dev via appsettings,
/// pra não expor esses detalhes aos usuários reais em produção.
/// </summary>
public class DebugMetricasOptions
{
    public bool Habilitado { get; init; } = false;

    /// <summary>Janela de contexto do modelo em uso (ex.: 1_000_000 para Sonnet 5) — usada só para calcular o % de uso.</summary>
    public int ContextWindowTokens { get; init; } = 1_000_000;
}
