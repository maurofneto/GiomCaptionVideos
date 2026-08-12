using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Infrastructure.Resiliencia;
using Giom.VideoManager.Testes.Comuns;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.Resiliencia;

/// <summary>
/// FallbackCaptionService (US #129, mesmo mecanismo de FallbackResumoService/US #106) — único
/// ICaptionService público, resolve a cadeia do canal via IEtapaProvedorConfigService (fake
/// aqui) e chama os ICaptionService Keyed na ordem que a config trouxe, não numa lista fixa
/// registrada no DI (Transcricao:FallbackChain antigo).
/// </summary>
public class FallbackCaptionServiceTests
{
    private sealed class FakeEtapaProvedorConfigService(IReadOnlyList<EtapaProvedorConfig> cadeia) : IEtapaProvedorConfigService
    {
        public Task<IReadOnlyList<EtapaProvedorConfig>> ObterCadeiaAsync(string channelId, string etapa, CancellationToken ct = default) =>
            Task.FromResult(cadeia);
    }

    private sealed class FakeCaptionProvider(bool falha, List<string> chamadas) : ICaptionService
    {
        public Task<Transcricao?> GetCaptionAsync(Video video, CancellationToken ct = default)
        {
            chamadas.Add(video.Id);
            if (falha) throw new InvalidOperationException("Falha simulada");
            return Task.FromResult<Transcricao?>(new Transcricao { ConteudoLimpo = "conteudo", Formato = "sbv", Origem = "Fake" });
        }
    }

    private static EtapaProvedorConfig Config(string provedor, int ordem) =>
        new("UCteste", "transcricao", provedor, ordem, Prompt: null, Model: null, MaxTokens: null, UsarObservabilidade: false, Origem: "Repository");

    private static Video CriarVideo() => new() { Id = "v1", Title = "Teste", ChannelId = "UCteste" };

    [Fact]
    public async Task GetCaptionAsync_PrimeiroProvedorDaCadeiaSucede_NaoTentaOsDemais()
    {
        var chamadas = new List<string>();
        var cadeia = new List<EtapaProvedorConfig> { Config("YouTubeCaption", 1), Config("YouTubeTranscript", 2) };

        var services = new ServiceCollection();
        services.AddKeyedSingleton<ICaptionService>("YouTubeCaption", new FakeCaptionProvider(falha: false, chamadas));
        services.AddKeyedSingleton<ICaptionService>("YouTubeTranscript", new FakeCaptionProvider(falha: false, chamadas));
        var sp = services.BuildServiceProvider();

        var fallback = new FallbackCaptionService(new FakeEtapaProvedorConfigService(cadeia), sp, new FakeLogger<FallbackCaptionService>());

        var transcricao = await fallback.GetCaptionAsync(CriarVideo());

        Assert.NotNull(transcricao);
        Assert.Single(chamadas);
    }

    [Fact]
    public async Task GetCaptionAsync_PrimeiroFalha_CaiParaOSegundoNaOrdemDaConfigDoCanal()
    {
        var chamadas = new List<string>();
        // Ordem invertida da "cadeia padrão" — prova que quem manda é o Ordem da config do
        // canal, não uma lista fixa registrada no DI.
        var cadeia = new List<EtapaProvedorConfig>
        {
            Config("Whisper", 1), Config("YtDlpCaption", 2), Config("YouTubeCaption", 3), Config("YouTubeTranscript", 4)
        };

        var services = new ServiceCollection();
        services.AddKeyedSingleton<ICaptionService>("Whisper", new FakeCaptionProvider(falha: true, chamadas));
        services.AddKeyedSingleton<ICaptionService>("YtDlpCaption", new FakeCaptionProvider(falha: false, chamadas));
        services.AddKeyedSingleton<ICaptionService>("YouTubeCaption", new FakeCaptionProvider(falha: false, chamadas));
        services.AddKeyedSingleton<ICaptionService>("YouTubeTranscript", new FakeCaptionProvider(falha: false, chamadas));
        var sp = services.BuildServiceProvider();

        var fallback = new FallbackCaptionService(new FakeEtapaProvedorConfigService(cadeia), sp, new FakeLogger<FallbackCaptionService>());

        var transcricao = await fallback.GetCaptionAsync(CriarVideo());

        Assert.NotNull(transcricao);
        Assert.Equal(2, chamadas.Count); // Whisper (falhou) + YtDlpCaption — os outros dois nem chegam a ser chamados
    }

    [Fact]
    public async Task GetCaptionAsync_TodosFalham_PropagaAExcecaoDoUltimoElo()
    {
        var chamadas = new List<string>();
        var cadeia = new List<EtapaProvedorConfig> { Config("YouTubeCaption", 1), Config("YouTubeTranscript", 2) };

        var services = new ServiceCollection();
        services.AddKeyedSingleton<ICaptionService>("YouTubeCaption", new FakeCaptionProvider(falha: true, chamadas));
        services.AddKeyedSingleton<ICaptionService>("YouTubeTranscript", new FakeCaptionProvider(falha: true, chamadas));
        var sp = services.BuildServiceProvider();

        var fallback = new FallbackCaptionService(new FakeEtapaProvedorConfigService(cadeia), sp, new FakeLogger<FallbackCaptionService>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => fallback.GetCaptionAsync(CriarVideo()));
    }
}
