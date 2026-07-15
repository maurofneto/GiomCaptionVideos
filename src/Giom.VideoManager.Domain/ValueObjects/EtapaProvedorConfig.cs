namespace Giom.VideoManager.Domain.ValueObjects;

/// <summary>
/// Config de execução de IA (prompt + parâmetros não-secretos) de um provedor específico
/// (Claude/Grok/OpenAI), pra uma etapa (resumo/categorização) de um canal — US #106. A ordem
/// (<see cref="Ordem"/>) entre as linhas de um mesmo (ChannelId, Etapa) É o FallbackChain daquele
/// canal: consultar todas ordenadas por Ordem já devolve a cadeia pronta, sem precisar de uma
/// lista separada. Model/MaxTokens nulos herdam o default do provedor (appsettings) — não
/// precisa de linha completa pra todo canal/provedor. UsarObservabilidade é sempre resolvido
/// (não-nulo) por quem monta esta config (Repositorio/ArquivoEtapaProvedorConfigService) —
/// roteamento é responsabilidade de transporte (ver Infrastructure.Helicone), não do provedor de
/// IA: nenhum Claude/Grok/OpenAiXxxService lê este campo.
/// </summary>
public record EtapaProvedorConfig(
    string ChannelId,
    string Etapa,
    string Provedor,
    int Ordem,
    string Prompt,
    string? Model,
    int? MaxTokens,
    bool UsarObservabilidade,
    string Origem);
