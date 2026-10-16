using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests;

/// <summary>Montagem do system prompt compartilhada pelos agentes Claude/Grok/OpenAI/Kimi.</summary>
public class PromptAgenteTests
{
    private const string PromptBase = "Você é o assistente do Instituto Giom.\n";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Montar_SemInstrucao_DevolveOPromptBaseIntacto(string? instrucao)
    {
        Assert.Same(PromptBase, PromptAgente.Montar(PromptBase, instrucao));
    }

    [Fact]
    public void Montar_ComInstrucao_AnexaSecaoFinalMantendoOPrefixo()
    {
        var prompt = PromptAgente.Montar(PromptBase, "  Responda em até 3 parágrafos.\n");

        Assert.StartsWith(PromptBase, prompt);
        Assert.Equal(PromptBase + "\n\n# Tamanho da resposta\nResponda em até 3 parágrafos.", prompt);
    }
}
