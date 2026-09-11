using Giom.VideoManager.Infrastructure.YouTube;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.YouTube;

/// <summary>
/// Garante que YouTubeCaptionService resolve YouTubeService keyed por ChannelId (US #146),
/// sem cair no singleton quando o keyed existe.
/// </summary>
public class YouTubeCaptionKeyedResolutionTests
{
    [Fact]
    public void Resolver_UsaKeyedServiceDoChannelId()
    {
        var keyedInstituto = new YouTubeService(new BaseClientService.Initializer { ApplicationName = "instituto-test" });
        var keyedMinisterio = new YouTubeService(new BaseClientService.Initializer { ApplicationName = "ministerio-test" });
        var singleton = new YouTubeService(new BaseClientService.Initializer { ApplicationName = "singleton-test" });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(singleton);
        services.AddKeyedSingleton("UC-instituto", keyedInstituto);
        services.AddKeyedSingleton("UC-ministerio", keyedMinisterio);
        services.AddSingleton(Options.Create(new YouTubeCaptionOptions { Formato = "vtt" }));
        services.AddSingleton<YouTubeCaptionService>();

        var sp = services.BuildServiceProvider();
        var caption = sp.GetRequiredService<YouTubeCaptionService>();

        // Exercita o resolver privado via GetCaptionAsync — a API vai falhar (sem OAuth),
        // mas o ApplicationName do client usado aparece na exceção/ou inspecionamos via
        // reflection do método privado ResolverYouTube.
        var resolver = typeof(YouTubeCaptionService)
            .GetMethod("ResolverYouTube", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        var resolvedInstituto = (YouTubeService)resolver.Invoke(caption, ["UC-instituto"])!;
        var resolvedMinisterio = (YouTubeService)resolver.Invoke(caption, ["UC-ministerio"])!;
        var resolvedSemCanal = (YouTubeService)resolver.Invoke(caption, [null])!;

        Assert.Same(keyedInstituto, resolvedInstituto);
        Assert.Same(keyedMinisterio, resolvedMinisterio);
        Assert.Same(singleton, resolvedSemCanal);
        Assert.NotSame(resolvedInstituto, resolvedMinisterio);
    }
}
