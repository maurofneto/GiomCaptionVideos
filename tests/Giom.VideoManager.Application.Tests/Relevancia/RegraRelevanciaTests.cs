using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Enums;
using Giom.VideoManager.Domain.Exceptions;
using Giom.VideoManager.Domain.ValueObjects;
using Xunit;

namespace Giom.VideoManager.Application.Tests.Relevancia;

/// <summary>
/// Regra de relevância dinâmica (#180): interpretação do JSON de canal_ingestao.regra_relevancia,
/// avaliação por campo/operador, grupos E/OU/NÃO e equivalência com o modelo antigo (enum).
/// </summary>
public class RegraRelevanciaTests
{
    private static Video CriarVideo(string titulo = "GIOM | Culto de domingo") => new()
    {
        Id = "v1", Title = titulo, Description = "Transmissão do culto", PrivacyStatus = "public",
        Type = VideoType.Upload, Transmissao = Transmissao.Nenhuma, Length = "01:20:15",
        UploadedAt = new DateTime(2026, 10, 18, 21, 30, 0)
    };

    private static bool Avalia(string regraJson, Video video) => RegraRelevancia.Parse(regraJson).Avalia(video);

    // ---------- condições de texto ----------

    [Theory]
    [InlineData("contem", "culto", true)]
    [InlineData("contem", "CULTO", true)]
    [InlineData("contem", "aula", false)]
    [InlineData("comeca_com", "giom", true)]
    [InlineData("comeca_com", "culto", false)]
    [InlineData("termina_com", "domingo", true)]
    [InlineData("termina_com", "GIOM", false)]
    [InlineData("igual", "giom | culto de domingo", true)]
    [InlineData("igual", "giom", false)]
    public void Titulo_OperadoresDeTexto_SemDiferenciarMaiusculas(string op, string valor, bool esperado) =>
        Assert.Equal(esperado, Avalia($$"""{"campo":"titulo","op":"{{op}}","valor":"{{valor}}"}""", CriarVideo()));

    [Fact]
    public void Titulo_Em_CasaSeQualquerValorBater() =>
        Assert.True(Avalia("""{"campo":"titulo","op":"em","valor":["GIOM | culto de domingo","outro"]}""", CriarVideo()));

    [Fact]
    public void Descricao_Contem() =>
        Assert.True(Avalia("""{"campo":"descricao","op":"contem","valor":"transmissão"}""", CriarVideo()));

    // ---------- listas fechadas ----------

    [Theory]
    [InlineData("privacidade", "igual", "\"public\"", true)]
    [InlineData("privacidade", "igual", "\"unlisted\"", false)]
    [InlineData("privacidade", "em", "[\"unlisted\",\"public\"]", true)]
    [InlineData("tipo", "igual", "\"upload\"", true)]
    [InlineData("tipo", "igual", "\"live\"", false)]
    [InlineData("transmissao", "igual", "\"nenhuma\"", true)]
    [InlineData("transmissao", "em", "[\"agendada\",\"ao_vivo\"]", false)]
    public void ListasFechadas_PrivacidadeTipoETransmissao(string campo, string op, string valorJson, bool esperado) =>
        Assert.Equal(esperado, Avalia($$"""{"campo":"{{campo}}","op":"{{op}}","valor":{{valorJson}}}""", CriarVideo()));

    [Theory]
    [InlineData(Transmissao.Agendada, true)]
    [InlineData(Transmissao.AoVivo, true)]
    [InlineData(Transmissao.Encerrada, false)]
    [InlineData(Transmissao.Nenhuma, false)]
    public void Transmissao_AgendadaOuAoVivo_IdentificaLiveQueAindaNaoTerminou(Transmissao transmissao, bool esperado)
    {
        var video = CriarVideo();
        video.Transmissao = transmissao;

        Assert.Equal(esperado, Avalia("""{"campo":"transmissao","op":"em","valor":["agendada","ao_vivo"]}""", video));
    }

    [Theory]
    [InlineData(VideoType.Live, "live", true)]
    [InlineData(VideoType.Short, "short", true)]
    [InlineData(VideoType.Premiere, "premiere", true)]
    [InlineData(VideoType.Upload, "live", false)]
    public void Tipo_ReconheceCadaTipoDeVideo(VideoType tipo, string valor, bool esperado)
    {
        var video = CriarVideo();
        video.Type = tipo;

        Assert.Equal(esperado, Avalia($$"""{"campo":"tipo","op":"igual","valor":"{{valor}}"}""", video));
    }

    // ---------- duração ----------

    [Theory]
    [InlineData("maior_igual", "300", "00:05:00", true)]
    [InlineData("maior_igual", "300", "00:04:59", false)]
    [InlineData("maior", "300", "00:05:00", false)]
    [InlineData("menor", "300", "00:04:59", true)]
    [InlineData("menor_igual", "300", "00:05:00", true)]
    [InlineData("igual", "60", "00:01:00", true)]
    [InlineData("maior_igual", "3600", "1.02:03:04", true)] // formato com dias
    public void Duracao_Comparacoes(string op, string valor, string length, bool esperado)
    {
        var video = CriarVideo();
        video.Length = length;

        Assert.Equal(esperado, Avalia($$"""{"campo":"duracao_segundos","op":"{{op}}","valor":{{valor}}}""", video));
    }

    [Theory]
    [InlineData("00:10:00", true)]
    [InlineData("00:05:00", true)]  // limites inclusivos
    [InlineData("00:20:00", true)]
    [InlineData("00:04:59", false)]
    [InlineData("00:20:01", false)]
    public void Duracao_Entre_ELimitesInclusivos(string length, bool esperado)
    {
        var video = CriarVideo();
        video.Length = length;

        Assert.Equal(esperado, Avalia("""{"campo":"duracao_segundos","op":"entre","valor":[300,1200]}""", video));
    }

    [Theory]
    [InlineData("")]
    [InlineData("não é duração")]
    public void Duracao_Desconhecida_NuncaCasa_NemEmMenorQue(string length)
    {
        var video = CriarVideo();
        video.Length = length;

        Assert.False(Avalia("""{"campo":"duracao_segundos","op":"menor","valor":300}""", video));
        Assert.False(Avalia("""{"campo":"duracao_segundos","op":"maior_igual","valor":0}""", video));
    }

    // ---------- data de envio ----------

    [Theory]
    [InlineData("antes", "\"2026-10-19\"", true)]
    [InlineData("antes", "\"2026-10-18\"", false)]   // mesmo dia não é "antes"
    [InlineData("depois", "\"2026-10-17\"", true)]
    [InlineData("depois", "\"2026-10-18\"", false)]
    [InlineData("entre", "[\"2026-10-18\",\"2026-10-18\"]", true)] // inclusivo nos dois lados
    [InlineData("entre", "[\"2026-10-01\",\"2026-10-17\"]", false)]
    public void EnviadoEm_ComparaPorDia(string op, string valorJson, bool esperado) =>
        Assert.Equal(esperado, Avalia($$"""{"campo":"enviado_em","op":"{{op}}","valor":{{valorJson}}}""", CriarVideo()));

    [Fact]
    public void EnviadoEm_Desconhecido_NuncaCasa()
    {
        var video = CriarVideo();
        video.UploadedAt = null;

        Assert.False(Avalia("""{"campo":"enviado_em","op":"antes","valor":"2030-01-01"}""", video));
    }

    // ---------- grupos ----------

    [Fact]
    public void Todas_ExigeTodasAsCondicoes()
    {
        const string regra = """{"todas":[{"campo":"titulo","op":"comeca_com","valor":"GIOM"},{"campo":"privacidade","op":"igual","valor":"public"}]}""";
        var video = CriarVideo();

        Assert.True(Avalia(regra, video));

        video.PrivacyStatus = "unlisted";
        Assert.False(Avalia(regra, video));
    }

    [Fact]
    public void Alguma_BastaUma()
    {
        const string regra = """{"alguma":[{"campo":"titulo","op":"contem","valor":"aula"},{"campo":"titulo","op":"contem","valor":"culto"}]}""";

        Assert.True(Avalia(regra, CriarVideo()));
        Assert.False(Avalia(regra, CriarVideo("Outra coisa")));
    }

    [Fact]
    public void Nao_InverteARegra()
    {
        const string regra = """{"nao":{"campo":"titulo","op":"contem","valor":"culto"}}""";

        Assert.False(Avalia(regra, CriarVideo()));
        Assert.True(Avalia(regra, CriarVideo("Aula de teologia")));
    }

    [Fact]
    public void GruposVazios_TodasVerdadeiro_AlgumaFalso()
    {
        Assert.True(Avalia("""{"todas":[]}""", CriarVideo()));
        Assert.False(Avalia("""{"alguma":[]}""", CriarVideo()));
    }

    [Fact]
    public void RegraCombinada_IgnoraLiveAgendadaEVideoCurto()
    {
        const string regra = """
            {"todas":[
              {"campo":"titulo","op":"comeca_com","valor":"GIOM"},
              {"nao":{"campo":"transmissao","op":"em","valor":["agendada","ao_vivo"]}},
              {"campo":"duracao_segundos","op":"maior_igual","valor":300}]}
            """;
        var video = CriarVideo();
        Assert.True(Avalia(regra, video));

        video.Transmissao = Transmissao.Agendada;
        video.Length = "00:00:00";
        Assert.False(Avalia(regra, video));
    }

    // ---------- JSON inválido ----------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("não é json")]
    [InlineData("[]")]                                                                    // raiz não é objeto
    [InlineData("""{"campo":"titulo","op":"contem"}""")]                                  // sem valor
    [InlineData("""{"campo":"inexistente","op":"contem","valor":"x"}""")]                 // campo desconhecido
    [InlineData("""{"campo":"titulo","op":"inexistente","valor":"x"}""")]                 // operador desconhecido
    [InlineData("""{"campo":"titulo","op":"maior","valor":"x"}""")]                       // operador de outro tipo de campo
    [InlineData("""{"campo":"duracao_segundos","op":"contem","valor":"x"}""")]
    [InlineData("""{"campo":"privacidade","op":"contem","valor":"public"}""")]           // lista fechada só igual/em
    [InlineData("""{"campo":"privacidade","op":"igual","valor":"publico"}""")]            // valor fora da lista
    [InlineData("""{"campo":"transmissao","op":"igual","valor":"agendado"}""")]
    [InlineData("""{"campo":"titulo","op":"contem","valor":""}""")]                       // texto vazio casaria tudo
    [InlineData("""{"campo":"titulo","op":"contem","valor":5}""")]                        // tipo errado
    [InlineData("""{"campo":"titulo","op":"em","valor":"x"}""")]                          // "em" exige lista
    [InlineData("""{"campo":"titulo","op":"em","valor":[]}""")]
    [InlineData("""{"campo":"duracao_segundos","op":"maior","valor":"300"}""")]           // número como texto
    [InlineData("""{"campo":"duracao_segundos","op":"maior","valor":-1}""")]
    [InlineData("""{"campo":"duracao_segundos","op":"entre","valor":[600,300]}""")]       // de > até
    [InlineData("""{"campo":"duracao_segundos","op":"entre","valor":[300]}""")]
    [InlineData("""{"campo":"enviado_em","op":"antes","valor":"18/10/2026"}""")]         // formato de data
    [InlineData("""{"campo":"enviado_em","op":"entre","valor":["2026-10-20","2026-10-01"]}""")]
    [InlineData("""{"campo":"titulo","op":"contem","valor":"x","extra":1}""")]            // chave extra
    [InlineData("""{"todas":[],"alguma":[]}""")]                                          // grupo com 2 chaves
    [InlineData("""{"todas":{}}""")]                                                      // grupo sem lista
    [InlineData("""{"qualquer":[]}""")]                                                   // chave desconhecida
    public void Parse_RegraInvalida_LancaRegraRelevanciaInvalidaException(string json) =>
        Assert.Throws<RegraRelevanciaInvalidaException>(() => RegraRelevancia.Parse(json));

    [Fact]
    public void Parse_AninhamentoAcimaDoLimite_Lanca()
    {
        var json = """{"campo":"titulo","op":"contem","valor":"x"}""";
        for (var i = 0; i < 10; i++) json = $$"""{"nao":{{json}}}""";

        Assert.Throws<RegraRelevanciaInvalidaException>(() => RegraRelevancia.Parse(json));
    }

    [Fact]
    public void Parse_MaisDeCemNos_Lanca()
    {
        var filhos = string.Join(",", Enumerable.Repeat("""{"campo":"titulo","op":"contem","valor":"x"}""", 101));

        Assert.Throws<RegraRelevanciaInvalidaException>(() => RegraRelevancia.Parse($$"""{"alguma":[{{filhos}}]}"""));
    }

    // ---------- equivalência com o modelo antigo (enum) ----------

    [Theory]
    [InlineData(VideoType.Live, "unlisted", true)]
    [InlineData(VideoType.Upload, "unlisted", false)]
    [InlineData(VideoType.Live, "public", false)]
    public void DeLegado_LiveUnlisted(VideoType tipo, string privacidade, bool esperado)
    {
        var video = CriarVideo();
        video.Type = tipo;
        video.PrivacyStatus = privacidade;

        Assert.Equal(esperado, RegraRelevancia.DeLegado(RelevanciaTipo.LiveUnlisted, [], null).Avalia(video));
    }

    [Theory]
    [InlineData("Culto de domingo", "public", true)]
    [InlineData("GIOM ao vivo", "public", true)]
    [InlineData("Um vídeo qualquer sobre GIOM", "public", false)] // "GIOM*" é prefixo
    [InlineData("GIOM | Culto", "unlisted", false)]               // filtro de privacidade
    public void DeLegado_TituloContemComPrefixoEFiltroDePrivacidade(string titulo, string privacidade, bool esperado)
    {
        var video = CriarVideo(titulo);
        video.PrivacyStatus = privacidade;

        var regra = RegraRelevancia.DeLegado(RelevanciaTipo.TituloContem, ["CULTO", "GIOM*"], "public");

        Assert.Equal(esperado, regra.Avalia(video));
    }

    /// <summary>
    /// JSONs gerados pela migration 20261027120000 a partir das linhas reais de canal_ingestao (Instituto:
    /// live_unlisted; Ministério: titulo_contem ["GIOM*"] + filtro public). Têm que se comportar como a
    /// regra antiga correspondente.
    /// </summary>
    [Theory]
    [InlineData("Culto", VideoType.Live, "unlisted", true)]
    [InlineData("Culto", VideoType.Upload, "unlisted", false)]
    [InlineData("Culto", VideoType.Live, "public", false)]
    public void RegraGeradaPelaMigration_Instituto_EquivaleAoLiveUnlisted(string titulo, VideoType tipo, string privacidade, bool esperado)
    {
        const string json = """{"todas": [{"op": "igual", "campo": "tipo", "valor": "live"}, {"op": "igual", "campo": "privacidade", "valor": "unlisted"}]}""";
        var video = CriarVideo(titulo);
        video.Type = tipo;
        video.PrivacyStatus = privacidade;

        Assert.Equal(esperado, Avalia(json, video));
        Assert.Equal(RegraRelevancia.DeLegado(RelevanciaTipo.LiveUnlisted, [], null).Avalia(video), Avalia(json, video));
    }

    [Theory]
    [InlineData("GIOM | Culto de domingo", "public", true)]
    [InlineData("Um vídeo qualquer sobre GIOM", "public", false)]
    [InlineData("GIOM | Culto de domingo", "unlisted", false)]
    public void RegraGeradaPelaMigration_Ministerio_EquivaleAoTituloContemComFiltro(string titulo, string privacidade, bool esperado)
    {
        const string json = """{"todas": [{"alguma": [{"op": "comeca_com", "campo": "titulo", "valor": "GIOM"}]}, {"op": "igual", "campo": "privacidade", "valor": "public"}]}""";
        var video = CriarVideo(titulo);
        video.PrivacyStatus = privacidade;

        Assert.Equal(esperado, Avalia(json, video));
        Assert.Equal(RegraRelevancia.DeLegado(RelevanciaTipo.TituloContem, ["GIOM*"], "public").Avalia(video), Avalia(json, video));
    }
}
