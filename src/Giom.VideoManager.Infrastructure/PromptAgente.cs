namespace Giom.VideoManager.Infrastructure;

/// <summary>
/// Monta o system prompt dos agentes de pergunta e resposta (Claude/Grok/OpenAI/Kimi) a partir
/// do agent.md. A instrução de concisão (tabela agente_config, via IAgenteConfigService) entra
/// só como seção final: sem ela o prompt é exatamente o agent.md, e com ela o agent.md continua
/// sendo o prefixo intacto — o prompt caching do provedor depende disso.
/// </summary>
public static class PromptAgente
{
    public const string CabecalhoConcisao = "\n\n# Tamanho da resposta\n";

    public static string Montar(string promptBase, string? instrucaoConcisao) =>
        string.IsNullOrWhiteSpace(instrucaoConcisao)
            ? promptBase
            : promptBase + CabecalhoConcisao + instrucaoConcisao.Trim();
}
