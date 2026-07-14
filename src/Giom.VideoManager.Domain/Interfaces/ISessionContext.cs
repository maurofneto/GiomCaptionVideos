namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>
/// Canal (AsyncLocal, mesmo padrão do IContextoTelemetria) pra qualquer implementação de IA
/// anunciar, antes de chamar o provedor, os metadados de sessão/usuário dessa chamada — uma
/// plataforma externa de observabilidade de LLM (ex.: Helicone, mas o Domain não sabe qual)
/// lê esse contexto e decide o que fazer com ele (ex.: traduzir pra headers HTTP). Sem isso,
/// a implementação concreta (ex.: ClaudeAgentService) precisaria conhecer diretamente qual
/// plataforma de observabilidade está configurada — o mesmo motivo pelo qual custo/tokens
/// passam por IContextoTelemetria em vez de cada Claude*Service falar com Sentry/Supabase.
/// </summary>
public interface ISessionContext
{
    void Definir(SessaoObservabilidade sessao);

    SessaoObservabilidade? Atual { get; }
}

/// <summary>
/// SessionId agrupa múltiplas chamadas relacionadas (ex.: todas as perguntas de um mesmo chat,
/// ou resumo+categorização de um mesmo vídeo) numa plataforma externa de observabilidade.
/// RequestId é gerado pelo chamador (não pela plataforma externa) especificamente pra permitir
/// reportar feedback depois sem precisar capturar nada da resposta do provedor de IA.
/// UsarObservabilidade (US #106) é decidido por quem resolve a EtapaProvedorConfig da chamada
/// (ex.: FallbackResumoService) e propagado por aqui — o adapter concreto de IA (Claude/Grok/
/// OpenAI) nunca lê esse campo, só a plataforma externa (via handler HTTP) decide o que fazer.
/// </summary>
public record SessaoObservabilidade(
    string SessionId,
    string SessionName,
    string SessionPath,
    string? UserId = null,
    IReadOnlyDictionary<string, string>? Propriedades = null,
    string? RequestId = null,
    bool UsarObservabilidade = false);
