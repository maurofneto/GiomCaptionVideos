using Xunit;

namespace Giom.VideoManager.Infrastructure.WhatsApp.Tests;

public class WhatsAppFormatadorTests
{
    [Fact]
    public void ParaWhatsApp_NegritoMarkdown_ViraAsteriscoSimples()
        => Assert.Equal("*importante*", WhatsAppFormatador.ParaWhatsApp("**importante**"));

    [Fact]
    public void ParaWhatsApp_ItalicoMarkdown_ViraUnderline()
        => Assert.Equal("_ênfase_", WhatsAppFormatador.ParaWhatsApp("*ênfase*"));

    [Fact]
    public void ParaWhatsApp_CodigoInline_ViraMonoespacado()
        => Assert.Equal("```dotnet build```", WhatsAppFormatador.ParaWhatsApp("`dotnet build`"));

    [Fact]
    public void ParaWhatsApp_Header_ViraNegrito()
        => Assert.Equal("*Título da seção*", WhatsAppFormatador.ParaWhatsApp("## Título da seção"));

    /// <summary>
    /// O caso que justifica os marcadores temporários do formatador: markdown usa "**" pra
    /// negrito e "*" pra itálico, e o WhatsApp usa "*" pra NEGRITO. Numa conversão ingênua, a
    /// regra de itálico casaria de novo no "*" que a de negrito acabou de produzir e o negrito
    /// viraria itálico.
    /// </summary>
    [Fact]
    public void ParaWhatsApp_NegritoEItalicoJuntos_NaoSeEmbaralham()
    {
        var resultado = WhatsAppFormatador.ParaWhatsApp("**negrito** e *itálico* na mesma linha");

        Assert.Equal("*negrito* e _itálico_ na mesma linha", resultado);
    }

    [Fact]
    public void ParaWhatsApp_Tabela_ViraListaSimples()
    {
        var markdown = "| Aula | Tema |\n|------|------|\n| 1 | Gênesis |";

        var resultado = WhatsAppFormatador.ParaWhatsApp(markdown);

        Assert.Equal("• Aula — Tema\n• 1 — Gênesis", resultado);
    }

    [Fact]
    public void ParaWhatsApp_TextoComCaractereDeHtml_NaoEscapaNada()
        => Assert.Equal("1 < 2 & 3 > 2", WhatsAppFormatador.ParaWhatsApp("1 < 2 & 3 > 2"));
}
