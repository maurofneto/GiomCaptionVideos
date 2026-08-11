using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Infrastructure.Precos;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.Precos;

/// <summary>
/// Substitui as antigas PricingCalculatorTests (Task #130 — dicionário estático virou
/// IPricingRepository/tabela provedor_preco). Mesmos casos, agora contra um repositório fake
/// em memória com os mesmos valores que a migration semeia.
/// </summary>
public class PricingServiceTests
{
    private sealed class FakePricingRepository : IPricingRepository
    {
        private static readonly Dictionary<(string, string), Preco> Tabela = new(new ProvedorModeloComparer())
        {
            [("Claude", "claude-sonnet-5")] = new("Claude", "claude-sonnet-5", 2.00m, 10.00m),
            [("Claude", "claude-haiku-4-5-20251001")] = new("Claude", "claude-haiku-4-5-20251001", 1.00m, 5.00m),
            [("OpenAI", "text-embedding-3-small")] = new("OpenAI", "text-embedding-3-small", 0.02m, 0m),
            [("OpenAI", "text-embedding-3-large")] = new("OpenAI", "text-embedding-3-large", 0.13m, 0m),
            [("OpenAI", "gpt-4o-mini")] = new("OpenAI", "gpt-4o-mini", 0.15m, 0.60m),
            [("VoyageAI", "voyage-3")] = new("VoyageAI", "voyage-3", 0.06m, 0m),
            [("VoyageAI", "voyage-3-lite")] = new("VoyageAI", "voyage-3-lite", 0.02m, 0m),
            [("Cohere", "embed-v4.0")] = new("Cohere", "embed-v4.0", 0.12m, 0m),
            [("Grok", "grok-3-mini")] = new("Grok", "grok-3-mini", 0.30m, 0.50m),
        };

        public Task<Preco?> ObterAsync(string provedor, string modelo, CancellationToken ct = default) =>
            Task.FromResult(Tabela.TryGetValue((provedor, modelo), out var preco) ? preco : null);

        private sealed class ProvedorModeloComparer : IEqualityComparer<(string Provedor, string Modelo)>
        {
            public bool Equals((string Provedor, string Modelo) x, (string Provedor, string Modelo) y) =>
                string.Equals(x.Provedor, y.Provedor, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Modelo, y.Modelo, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode((string Provedor, string Modelo) obj) =>
                HashCode.Combine(obj.Provedor.ToUpperInvariant(), obj.Modelo.ToUpperInvariant());
        }
    }

    private static readonly PricingService Service = new(new FakePricingRepository(), NullLogger<PricingService>.Instance);

    private sealed class FalhandoPricingRepository : IPricingRepository
    {
        public Task<Preco?> ObterAsync(string provedor, string modelo, CancellationToken ct = default) =>
            throw new HttpRequestException("Supabase indisponível (simulado)");
    }

    [Fact]
    public async Task CalcularAsync_RepositorioFalha_RetornaNullEmVezDePropagar()
    {
        // Diferente do antigo PricingCalculator (dicionário puro, nunca falhava) — a versão
        // orientada a banco pode ter erro de rede/tabela ainda não migrada; isso não pode
        // derrubar a etapa (resumo/categorização/embedding) só porque a telemetria de custo falhou.
        var service = new PricingService(new FalhandoPricingRepository(), NullLogger<PricingService>.Instance);

        var custo = await service.CalcularAsync("Claude", "claude-sonnet-5", 1000, 1000);

        Assert.Null(custo);
    }

    [Fact]
    public async Task CalcularAsync_ProvedorOuModeloDesconhecido_RetornaNull()
    {
        Assert.Null(await Service.CalcularAsync("Claude", "modelo-que-nao-existe", 1000, 1000));
        Assert.Null(await Service.CalcularAsync("provedor-que-nao-existe", "claude-sonnet-5", 1000, 1000));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CalcularAsync_ModeloVazioOuNulo_RetornaNull(string? modelo)
    {
        Assert.Null(await Service.CalcularAsync("Claude", modelo, 1000, 1000));
    }

    [Fact]
    public async Task CalcularAsync_ClaudeSonnetComTokensConhecidos_CalculaCustoExato()
    {
        // Sonnet 5: US$2,00 entrada / US$10,00 saída por 1M tokens
        var custo = await Service.CalcularAsync("Claude", "claude-sonnet-5", tokensEntrada: 1_000_000, tokensSaida: 1_000_000);

        Assert.Equal(12.00m, custo);
    }

    [Fact]
    public async Task CalcularAsync_ClaudeHaikuComTokensConhecidos_CalculaCustoExato()
    {
        // Haiku 4.5: US$1,00 entrada / US$5,00 saída por 1M tokens
        var custo = await Service.CalcularAsync("Claude", "claude-haiku-4-5-20251001", tokensEntrada: 500_000, tokensSaida: 200_000);

        Assert.Equal(0.50m + 1.00m, custo);
    }

    [Fact]
    public async Task CalcularAsync_TokensNulos_TrataComoZero()
    {
        Assert.Equal(0m, await Service.CalcularAsync("Claude", "claude-sonnet-5", tokensEntrada: null, tokensSaida: null));
    }

    [Fact]
    public async Task CalcularAsync_SoTokensDeEntrada_IgnoraSaida()
    {
        var custo = await Service.CalcularAsync("Claude", "claude-sonnet-5", tokensEntrada: 1_000_000, tokensSaida: null);

        Assert.Equal(2.00m, custo);
    }

    [Theory]
    [InlineData("OpenAI", "text-embedding-3-small", 1_000_000, 0.02)]
    [InlineData("OpenAI", "text-embedding-3-large", 500_000, 0.065)]
    [InlineData("VoyageAI", "voyage-3", 1_000_000, 0.06)]
    [InlineData("VoyageAI", "voyage-3-lite", 1_000_000, 0.02)]
    [InlineData("Cohere", "embed-v4.0", 1_000_000, 0.12)]
    public async Task CalcularAsync_Embeddings_SoEntrada_CalculaCustoExato(string provedor, string modelo, int tokens, double custoEsperado)
    {
        var custo = await Service.CalcularAsync(provedor, modelo, tokens);

        Assert.Equal((decimal)custoEsperado, custo);
    }

    [Fact]
    public async Task CalcularAsync_Grok_TinhaFicadoSemPreco_AgoraTemPreco()
    {
        var custo = await Service.CalcularAsync("Grok", "grok-3-mini", tokensEntrada: 1_000_000, tokensSaida: 1_000_000);

        Assert.NotNull(custo);
        Assert.Equal(0.30m + 0.50m, custo);
    }

    [Fact]
    public async Task CalcularAsync_OpenAiChat_TinhaFicadoSemPreco_AgoraTemPreco()
    {
        var custo = await Service.CalcularAsync("OpenAI", "gpt-4o-mini", tokensEntrada: 1_000_000, tokensSaida: 1_000_000);

        Assert.NotNull(custo);
        Assert.Equal(0.15m + 0.60m, custo);
    }

    [Fact]
    public async Task CalcularAsync_ProvedorEModeloSaoCaseInsensitive()
    {
        var custoMinusculo = await Service.CalcularAsync("claude", "claude-sonnet-5", 1000, 1000);
        var custoMaiusculo = await Service.CalcularAsync("CLAUDE", "CLAUDE-SONNET-5", 1000, 1000);

        Assert.Equal(custoMinusculo, custoMaiusculo);
    }
}
