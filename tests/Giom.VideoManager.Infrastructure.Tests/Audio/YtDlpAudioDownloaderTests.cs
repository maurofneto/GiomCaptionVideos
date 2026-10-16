using Giom.VideoManager.Infrastructure.Audio;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.Audio;

public class YtDlpAudioDownloaderTests
{
    [Fact]
    public void SemCookies_MontaArgumentosSemCookiesFlag()
    {
        var args = YtDlpAudioDownloader.MontarArgumentos("abc123DEF45", "/tmp/w/%(id)s.%(ext)s", cookiesPath: null);

        Assert.Equal("-x --audio-format mp3 -o \"/tmp/w/%(id)s.%(ext)s\" https://www.youtube.com/watch?v=abc123DEF45", args);
        Assert.DoesNotContain("--cookies", args);
    }

    [Fact]
    public void ComCookies_AntepoeCookiesFlagEntreAspas()
    {
        var args = YtDlpAudioDownloader.MontarArgumentos("abc123DEF45", "/tmp/w/%(id)s.%(ext)s", "/tmp/yt-dlp-cookies-instituto.txt");

        Assert.StartsWith("--cookies \"/tmp/yt-dlp-cookies-instituto.txt\" -x --audio-format mp3", args);
    }

    [Theory]
    [InlineData("ERROR: [youtube] abc: Sign in to confirm you’re not a bot. Use --cookies-from-browser", true)]
    [InlineData("ERROR: [youtube] abc: sign in to confirm you're not a bot", true)]
    [InlineData("ERROR: unable to download video data: HTTP Error 403: Forbidden", false)]
    [InlineData("", false)]
    public void DetectaBloqueioAntiBot(string stderr, bool esperado)
    {
        Assert.Equal(esperado, YtDlpAudioDownloader.IndicaBloqueioAntiBot(stderr));
    }
}
