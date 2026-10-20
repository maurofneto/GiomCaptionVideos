using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Giom.VideoManager.Infrastructure.GrokVoice.Tests;

public class RegistroDiTests
{
    private sealed class PrecoNulo : IPricingService
    {
        public Task<decimal?> CalcularAsync(string provedor, string? modelo, int? tokensEntrada, int? tokensSaida = null, CancellationToken ct = default) => Task.FromResult<decimal?>(null);
    }

    private static ServiceProvider Montar(Dictionary<string, string?>? config = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config ?? []).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IPricingService, PrecoNulo>(); // em produção vem de AddPricingService (Composition)
        services.AddGrokVoiceTranscription(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void ElosKeyedGrokVoice_ResolvePeloNomeUsadoEmEtapaProvedorConfig()
    {
        using var provider = Montar();

        // Mesma chamada do FallbackCaptionService: GetRequiredKeyedService<ICaptionService>(config.Provedor).
        var servico = provider.GetRequiredKeyedService<ICaptionService>("GrokVoice");

        Assert.IsType<GrokVoiceCaptionService>(servico);
    }

    [Fact]
    public void DownloadDeAudio_ERegistradoSemCookiesPorCanal()
    {
        using var provider = Montar();

        Assert.NotNull(provider.GetRequiredService<IAudioDownloader>());
        Assert.Null(provider.GetRequiredService<IChannelYtDlpCookies>().GetCookiesFilePath("UCqualquer"));
    }

    [Fact]
    public void Options_VemDaSecaoGrokVoice_ComDefaultsSeguros()
    {
        using var provider = Montar(new()
        {
            ["GrokVoice:ApiKey"] = "xai-teste",
            ["GrokVoice:Language"] = "pt",
        });

        var options = provider.GetRequiredService<IOptions<GrokVoiceOptions>>().Value;

        Assert.Equal("xai-teste", options.ApiKey);
        Assert.Equal("grok-voice-transcribe-2.0", options.Model);
        Assert.Equal("https://api.x.ai/", options.BaseUrl);
    }

    [Fact]
    public void RegistrarDuasVezes_NaoDuplicaODownload()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGrokVoiceTranscription(configuration);
        services.AddGrokVoiceTranscription(configuration);

        Assert.Single(services, d => d.ServiceType == typeof(IAudioDownloader));
    }
}
