using Giom.VideoManager.Domain.Exceptions;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure.Audio;
using Giom.VideoManager.Infrastructure.YtDlp;
using Giom.VideoManager.Testes.Comuns;
using Microsoft.Extensions.Options;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.Audio;

public class YtDlpAudioDownloaderTests
{
    [Fact]
    public void SemCookies_MontaArgumentosSemCookiesFlag()
    {
        var args = YtDlpAudioDownloader.MontarArgumentos("abc123DEF45", "/tmp/w/%(id)s.%(ext)s", cookiesPath: null);

        Assert.Equal(["-x", "--audio-format", "mp3", "-o", "/tmp/w/%(id)s.%(ext)s", "https://www.youtube.com/watch?v=abc123DEF45"], args);
        Assert.DoesNotContain("--cookies", args);
    }

    [Fact]
    public void ComCookies_AntepoeCookiesFlag_UmItemPorArgumento()
    {
        var args = YtDlpAudioDownloader.MontarArgumentos("abc123DEF45", "/tmp/w/%(id)s.%(ext)s", "/tmp/yt-dlp cookies instituto.txt");

        Assert.Equal(["--cookies", "/tmp/yt-dlp cookies instituto.txt"], args.Take(2));
        Assert.Equal("https://www.youtube.com/watch?v=abc123DEF45", args[^1]);
    }

    [Fact]
    public void ProxyEPoToken_NaoEntramNosArgumentosEspecificos_SoNoYtDlpProcesso()
    {
        var args = YtDlpAudioDownloader.MontarArgumentos("abc123DEF45", "/tmp/w/%(id)s.%(ext)s", cookiesPath: null);

        Assert.DoesNotContain("--proxy", args);
        Assert.DoesNotContain("--extractor-args", args);
    }

    [Fact]
    public async Task BloqueioAntiBot_ViraFalhaNaoTransitoria_ComReason()
    {
        // Executável inexistente nunca chega a este caminho; aqui o yt-dlp "falso" é um script que
        // imprime o bloqueio no stderr e sai com 1.
        var dir = Directory.CreateTempSubdirectory("ytdlp-audio-teste-").FullName;
        try
        {
            var falso = OperatingSystem.IsWindows() ? Path.Combine(dir, "ytdlp-falso.cmd") : Path.Combine(dir, "ytdlp-falso.sh");
            const string erro = "ERROR: [youtube] abc: Sign in to confirm you're not a bot";
            if (OperatingSystem.IsWindows())
                File.WriteAllLines(falso, ["@echo off", $"echo {erro} 1>&2", "exit /b 1"]);
            else
            {
                File.WriteAllLines(falso, ["#!/bin/sh", $"echo \"{erro}\" >&2", "exit 1"]);
                File.SetUnixFileMode(falso, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var downloader = new YtDlpAudioDownloader(
                Options.Create(new YtDlpAudioOptions { YtDlpPath = falso }),
                Options.Create(new YtDlpOptions()),
                new NullChannelYtDlpCookies(),
                new FakeLogger<YtDlpAudioDownloader>());

            var falha = await Assert.ThrowsAsync<FalhaNaoTransitoriaException>(
                () => downloader.BaixarAudioAsync("abc123DEF45", "UC1", dir));

            Assert.Equal(YtDlpBloqueioBot.ReasonSignInNotABot, falha.Reason);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
