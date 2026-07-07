using Xunit;

namespace Giom.VideoManager.Infrastructure.Telegram.Tests;

public class TelegramFormatadorTests
{
    [Fact]
    public void ParaHtml_Negrito_ConverteParaTagB()
    {
        var html = TelegramFormatador.ParaHtml("isso é **importante** aqui");

        Assert.Equal("isso é <b>importante</b> aqui", html);
    }

    [Fact]
    public void ParaHtml_Italico_ConverteParaTagI()
    {
        var html = TelegramFormatador.ParaHtml("isso é *importante* aqui");

        Assert.Equal("isso é <i>importante</i> aqui", html);
    }

    [Fact]
    public void ParaHtml_NegritoEItalicoNaoSeConfundem()
    {
        var html = TelegramFormatador.ParaHtml("**negrito** e *itálico* juntos");

        Assert.Equal("<b>negrito</b> e <i>itálico</i> juntos", html);
    }

    [Fact]
    public void ParaHtml_CodigoInline_ConverteParaTagCode()
    {
        var html = TelegramFormatador.ParaHtml("use o comando `git status` no terminal");

        Assert.Equal("use o comando <code>git status</code> no terminal", html);
    }

    [Fact]
    public void ParaHtml_Header_ViraNegrito()
    {
        var html = TelegramFormatador.ParaHtml("## Título da Seção");

        Assert.Equal("<b>Título da Seção</b>", html);
    }

    [Theory]
    [InlineData("1 < 2 e 3 > 2", "1 &lt; 2 e 3 &gt; 2")]
    [InlineData("Tom & Jerry", "Tom &amp; Jerry")]
    public void ParaHtml_EscapaCaracteresReservadosDeHtml(string entrada, string esperado)
    {
        Assert.Equal(esperado, TelegramFormatador.ParaHtml(entrada));
    }

    [Fact]
    public void ParaHtml_Tabela_ViraListaComMarcadores()
    {
        var markdown = "| Área | Peso |\n|------|------|\n| Escatologia | 0.9 |";

        var html = TelegramFormatador.ParaHtml(markdown);

        Assert.DoesNotContain("|", html);
        Assert.Contains("• Área — Peso", html);
        Assert.Contains("• Escatologia — 0.9", html);
    }

    [Fact]
    public void ParaHtml_TextoSemMarkdown_PermaneceInalterado()
    {
        var texto = "Isso é só um texto simples, sem formatação nenhuma.";

        Assert.Equal(texto, TelegramFormatador.ParaHtml(texto));
    }
}
