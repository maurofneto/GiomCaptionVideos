using Giom.VideoManager.Infrastructure.Incerteza;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.Incerteza;

public class HeuristicaIncertezaDetectorServiceTests
{
    private readonly HeuristicaIncertezaDetectorService _detector = new();

    [Theory]
    [InlineData("Não sei responder isso com certeza, mas procure o Dr. Wagner.")]
    [InlineData("NÃO TENHO CERTEZA sobre esse ponto específico.")] // case-insensitive
    [InlineData("Não consigo confirmar essa informação nas aulas.")]
    [InlineData("Vale a pena perguntar diretamente ao Dr. Wagner sobre isso.")]
    public void ContemIncerteza_ComSinalConhecido_RetornaTrue(string texto)
    {
        Assert.True(_detector.ContemIncerteza(texto));
    }

    [Theory]
    [InlineData("A escatologia estuda os eventos finais, segundo o ensino do Dr. Wagner.")]
    [InlineData("")]
    public void ContemIncerteza_SemSinalNenhum_RetornaFalse(string texto)
    {
        Assert.False(_detector.ContemIncerteza(texto));
    }
}
