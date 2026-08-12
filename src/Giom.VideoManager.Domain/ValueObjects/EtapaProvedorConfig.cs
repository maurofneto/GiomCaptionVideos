namespace Giom.VideoManager.Domain.ValueObjects;

/// <summary>
/// Config de execução de um provedor específico (Claude/Grok/OpenAI/Kimi/VoyageAI/Cohere/
/// YouTubeCaption/YouTubeTranscript/YtDlpCaption/Whisper), pra uma etapa (resumo/categorização/
/// embedding/transcricao — US #106 e #129) de um canal. A ordem (<see cref="Ordem"/>) entre as
/// linhas de um mesmo (ChannelId, Etapa) É o FallbackChain daquele canal: consultar todas
/// ordenadas por Ordem já devolve a cadeia pronta, sem precisar de uma lista separada.
/// Model/MaxTokens nulos herdam o default do provedor (appsettings) — não precisa de linha
/// completa pra todo canal/provedor. Prompt é nulo pra Embedding/Transcrição (não geram texto via
/// LLM — vetorização e extração de legenda/áudio, respectivamente). UsarObservabilidade é sempre
/// resolvido (não-nulo) por quem monta esta config (Repositorio/ArquivoEtapaProvedorConfigService)
/// — roteamento é responsabilidade de transporte (ver Infrastructure.Helicone), não do provedor:
/// nenhum XxxService lê este campo.
/// </summary>
public record EtapaProvedorConfig(
    string ChannelId,
    string Etapa,
    string Provedor,
    int Ordem,
    string? Prompt,
    string? Model,
    int? MaxTokens,
    bool UsarObservabilidade,
    string Origem);
