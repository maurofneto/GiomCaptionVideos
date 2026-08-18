using Giom.VideoManager.Infrastructure.Canais;
using Xunit;

namespace Giom.VideoManager.Infrastructure.Tests.Canais;

/// <summary>
/// Regra compartilhada por todos os canais de chat (Telegram, WhatsApp) — nasceu no Telegram,
/// onde dois bugs reais já aconteceram (Board #56/#57: resposta cortada no primeiro bloco de
/// texto, e mensagem estourando os 4096 caracteres do canal). Estes testes existem
/// especificamente pra essas duas classes de bug nunca voltarem sem quebrar um teste antes —
/// agora valendo pros dois canais de uma vez.
/// </summary>
public class DivisorDeMensagemTests
{
    [Fact]
    public void DividirEmPartes_TextoDentroDoLimite_DevolveUmaUnicaParteInalterada()
    {
        var texto = "uma resposta curta, bem dentro do limite";

        var partes = DivisorDeMensagem.DividirEmPartes(texto, limite: 100);

        Assert.Equal([texto], partes);
    }

    [Fact]
    public void DividirEmPartes_DoisParagrafosQueJuntosEstouramOLimite_DivideNaQuebraDeParagrafo()
    {
        var texto = "abcdefghij\n\nklmnopqrst"; // dois parágrafos de 10 chars cada

        var partes = DivisorDeMensagem.DividirEmPartes(texto, limite: 10);

        Assert.Equal(["abcdefghij", "klmnopqrst"], partes);
    }

    [Fact]
    public void DividirEmPartes_UmUnicoParagrafoMaiorQueOLimite_CortaEmPedacosDoTamanhoDoLimite()
    {
        var texto = "abcdefghij"; // 10 chars, sem quebra de parágrafo nenhuma

        var partes = DivisorDeMensagem.DividirEmPartes(texto, limite: 5);

        Assert.Equal(["abcde", "fghij"], partes);
    }

    [Fact]
    public void DividirEmPartes_NuncaPerdeNemDuplicaConteudo()
    {
        var texto = string.Join("\n\n", Enumerable.Range(1, 20).Select(i => new string((char)('a' + i % 26), 137)));

        var partes = DivisorDeMensagem.DividirEmPartes(texto, limite: 500);

        // O separador "\n\n" entre duas partes que viraram mensagens SEPARADAS pode se perder
        // (cada parte já é visualmente distinta por ser outra mensagem) — o que não pode
        // acontecer é perder ou duplicar o CONTEÚDO real.
        Assert.Equal(texto.Replace("\n\n", ""), string.Join("", partes).Replace("\n\n", ""));
        Assert.All(partes, p => Assert.True(p.Length <= 500));
    }
}
