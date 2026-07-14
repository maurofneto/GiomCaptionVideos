using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Microsoft.Extensions.Configuration;

namespace Giom.VideoManager.Infrastructure.Observabilidade;

/// <summary>
/// Implementação de IEtapaProvedorConfigService que lê de arquivo (Prompts/{etapa}.md via
/// PromptLoader) + Resumo:FallbackChain/Categorizacao:FallbackChain do appsettings — o caminho
/// de hoje, preservado (US #106) como opção via EtapaProvedorConfig:Provider=Arquivo, sem
/// Supabase. Model/MaxTokens sempre null: quem chama já cai pro default do próprio provedor (ver
/// ClaudeResumoService etc.), igual sempre foi — só o prompt e a ordem do fallback vêm daqui.
/// UsarObservabilidade vem de Observabilidade:UsarPadrao (um valor só, não varia por provedor
/// neste modo — Repository é quem permite granularidade por linha, ver
/// RepositorioEtapaProvedorConfigService). Não varia por canal (mesmo prompt pra todos, igual hoje).
/// </summary>
public sealed class ArquivoEtapaProvedorConfigService(IConfiguration config, PromptLoader prompts) : IEtapaProvedorConfigService
{
    public Task<IReadOnlyList<EtapaProvedorConfig>> ObterCadeiaAsync(string channelId, string etapa, CancellationToken ct = default)
    {
        var chaveFallbackChain = etapa == "categorizacao" ? "Categorizacao:FallbackChain" : "Resumo:FallbackChain";
        var cadeiaNomes = config.GetSection(chaveFallbackChain).Get<string[]>() ?? ["Claude", "Grok", "OpenAI"];
        var prompt = prompts.Load($"{etapa}.md");
        var usarObservabilidade = config.GetValue("Observabilidade:UsarPadrao", false);

        IReadOnlyList<EtapaProvedorConfig> cadeia = cadeiaNomes
            .Select((nome, indice) => new EtapaProvedorConfig(
                channelId, etapa, nome, Ordem: indice + 1,
                prompt, Model: null, MaxTokens: null, UsarObservabilidade: usarObservabilidade, Origem: "Arquivo"))
            .ToList();

        return Task.FromResult(cadeia);
    }
}
