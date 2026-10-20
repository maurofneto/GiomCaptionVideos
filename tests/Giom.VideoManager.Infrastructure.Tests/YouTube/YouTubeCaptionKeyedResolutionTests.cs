using Giom.VideoManager.Infrastructure.YouTube;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.YouTube;

/// <summary>
/// Garante que YouTubeCaptionService resolve YouTubeService keyed por ChannelId (US #146) e que
/// canal sem credencial falha com erro classificado — nunca cai num YouTubeService global.
/// </summary>
public class YouTubeCaptionKeyedResolutionTests
{
    private static YouTubeCaptionService Criar(Action<ServiceCollection> configurar)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new YouTubeCaptionOptions { Formato = "vtt" }));
        services.AddSingleton<YouTubeCaptionService>();
        configurar(services);
        return services.BuildServiceProvider().GetRequiredService<YouTubeCaptionService>();
    }

    [Fact]
    public void Resolver_UsaKeyedServiceDoChannelId_SemUsarSingleton()
    {
        var keyedInstituto = new YouTubeService(new BaseClientService.Initializer { ApplicationName = "instituto-test" });
        var keyedMinisterio = new YouTubeService(new BaseClientService.Initializer { ApplicationName = "ministerio-test" });
        var singleton = new YouTubeService(new BaseClientService.Initializer { ApplicationName = "singleton-test" });

        var caption = Criar(s =>
        {
            s.AddSingleton(singleton);
            s.AddKeyedSingleton("UC-instituto", keyedInstituto);
            s.AddKeyedSingleton("UC-ministerio", keyedMinisterio);
        });

        Assert.Same(keyedInstituto, caption.ResolverYouTube("UC-instituto"));
        Assert.Same(keyedMinisterio, caption.ResolverYouTube("UC-ministerio"));
    }

    [Fact]
    public void Resolver_CanalSemCredencial_LancaExcecaoClassificadaComMotivo_MesmoComSingleton()
    {
        var keyedMinisterio = new YouTubeService(new BaseClientService.Initializer { ApplicationName = "ministerio-test" });
        var singleton = new YouTubeService(new BaseClientService.Initializer { ApplicationName = "singleton-test" });

        var caption = Criar(s =>
        {
            s.AddSingleton(singleton);
            s.AddKeyedSingleton("UC-ministerio", keyedMinisterio);
            s.AddSingleton(new FalhasCredencialPorCanal(new Dictionary<string, string> { ["UC-instituto"] = "invalid_grant" }));
        });

        var ex = Assert.Throws<CredencialCanalIndisponivelException>(() => caption.ResolverYouTube("UC-instituto"));
        Assert.Equal("UC-instituto", ex.ChannelId);
        Assert.Equal("invalid_grant", ex.Motivo);
        Assert.Contains("invalid_grant", ex.Message);

        // O outro canal continua resolvendo o seu keyed.
        Assert.Same(keyedMinisterio, caption.ResolverYouTube("UC-ministerio"));
    }

    [Fact]
    public void Resolver_CanalDesconhecido_LancaExcecaoClassificada_NaoErroDeDI()
    {
        var caption = Criar(_ => { });

        var ex = Assert.Throws<CredencialCanalIndisponivelException>(() => caption.ResolverYouTube("UC-outro"));
        Assert.Equal("UC-outro", ex.ChannelId);
        Assert.DoesNotContain("No service for type", ex.Message);
    }

    [Fact]
    public void Resolver_SemChannelId_LancaExcecaoClassificada()
    {
        var caption = Criar(_ => { });

        Assert.Throws<CredencialCanalIndisponivelException>(() => caption.ResolverYouTube(null));
    }
}
