using Giom.VideoManager.Infrastructure.YouTube;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.YouTube;

public class YtDlpCookiesPorCanalTests
{
    [Fact]
    public void AddYtDlpCookiesPorCanal_ArquivosDistintosPorCanal()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["instituto:YtDlp:CookiesContent"] = "# Netscape\ninstituto-cookie",
                ["ministerio:YtDlp:CookiesContent"] = "# Netscape\nministerio-cookie",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddYtDlpCookiesPorCanal(
            config,
            [("UC-instituto", "instituto"), ("UC-ministerio", "ministerio")],
            NullLogger.Instance);

        var cookies = services.BuildServiceProvider().GetRequiredService<IChannelYtDlpCookies>();

        var pathInstituto = cookies.GetCookiesFilePath("UC-instituto");
        var pathMinisterio = cookies.GetCookiesFilePath("UC-ministerio");

        Assert.False(string.IsNullOrWhiteSpace(pathInstituto));
        Assert.False(string.IsNullOrWhiteSpace(pathMinisterio));
        Assert.NotEqual(pathInstituto, pathMinisterio);
        Assert.Equal("# Netscape\ninstituto-cookie", File.ReadAllText(pathInstituto!));
        Assert.Equal("# Netscape\nministerio-cookie", File.ReadAllText(pathMinisterio!));
    }

    [Fact]
    public void AddYtDlpCookiesPorCanal_FallbackLegadoPipelineQuandoCanalVazio()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["YtDlp:CookiesContent"] = "# Netscape\nlegado",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddYtDlpCookiesPorCanal(
            config,
            [("UC-instituto", "instituto")],
            NullLogger.Instance);

        var cookies = services.BuildServiceProvider().GetRequiredService<IChannelYtDlpCookies>();
        var path = cookies.GetCookiesFilePath("UC-instituto");

        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.Equal("# Netscape\nlegado", File.ReadAllText(path!));
    }
}
