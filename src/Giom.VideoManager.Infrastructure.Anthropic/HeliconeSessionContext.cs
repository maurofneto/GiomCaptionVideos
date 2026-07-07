namespace Giom.VideoManager.Infrastructure.Anthropic;

/// <summary>
/// Canal (AsyncLocal, mesmo padrão do ContextoTelemetria) pra cada Claude*Service anunciar,
/// antes de chamar a API, em qual "sessão" do Helicone essa chamada entra — o
/// HeliconeHeadersHandler (DelegatingHandler na pipeline do HttpClient "anthropic") lê esse
/// contexto e injeta os headers na requisição de saída. Sem isso, os headers de sessão teriam
/// que ser fixos por HttpClient (definidos uma vez na criação), mas cada chamada do bot é uma
/// conversa/vídeo diferente — precisa variar por requisição.
/// </summary>
public class HeliconeSessionContext
{
    private static readonly AsyncLocal<HeliconeSessao?> _atual = new();

    public void Definir(HeliconeSessao sessao) => _atual.Value = sessao;

    public HeliconeSessao? Atual => _atual.Value;
}

/// <summary>
/// SessionId agrupa múltiplas chamadas relacionadas (ex.: todas as perguntas de um mesmo chat,
/// ou resumo+categorização de um mesmo vídeo) na visão "Sessions" do Helicone. Propriedades
/// vão além do Environment fixo — filtros adicionais no dashboard (canal, tipo de operação).
/// RequestId é gerado por nós (não pelo Helicone) especificamente pra poder reportar feedback
/// depois — ver HeliconeFeedbackService.
/// </summary>
public record HeliconeSessao(
    string SessionId,
    string SessionName,
    string SessionPath,
    string? UserId = null,
    IReadOnlyDictionary<string, string>? Propriedades = null,
    string? RequestId = null);
