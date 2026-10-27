using System.Globalization;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Enums;

namespace Giom.VideoManager.Domain.ValueObjects;

/// <summary>Campo do vídeo que uma condição de relevância examina.</summary>
public enum CampoRelevancia
{
    Titulo,
    Descricao,
    Privacidade,
    Tipo,
    Transmissao,
    DuracaoSegundos,
    EnviadoEm
}

/// <summary>Operador de uma condição. Cada campo aceita só um subconjunto (validado em <see cref="RegraRelevanciaParser"/>).</summary>
public enum OperadorRelevancia
{
    Contem,
    ComecaCom,
    TerminaCom,
    Igual,
    Em,
    Maior,
    MaiorIgual,
    Menor,
    MenorIgual,
    Entre,
    Antes,
    Depois
}

/// <summary>
/// Regra de relevância de um canal como árvore de condições (filtro dinâmico, vindo do banco). Um nó
/// é um grupo E (<see cref="RegraTodas"/>), um grupo OU (<see cref="RegraAlguma"/>), uma negação
/// (<see cref="RegraNao"/>) ou uma condição sobre um campo do vídeo. Vem de canal_ingestao.regra_relevancia
/// (JSON, ver <see cref="Parse"/>); canais sem regra caem em <see cref="DeLegado"/>, que reproduz o
/// comportamento do enum <see cref="RelevanciaTipo"/> + PrivacyStatusFiltro.
/// </summary>
public abstract record RegraRelevancia
{
    /// <summary>true se o vídeo deve entrar no pipeline.</summary>
    public abstract bool Avalia(Video video);

    /// <summary>Interpreta o JSON de canal_ingestao.regra_relevancia; lança <see cref="Exceptions.RegraRelevanciaInvalidaException"/> se inválido.</summary>
    public static RegraRelevancia Parse(string json) => RegraRelevanciaParser.Parse(json);

    /// <summary>
    /// Regra equivalente ao modelo antigo (enum + valores + filtro de privacidade): LiveUnlisted =
    /// tipo live E unlisted; TituloContem = algum valor casa o título (terminando em "*" é prefixo, sem
    /// "*" é substring); e, se houver filtro de privacidade, E a privacidade igual a ele.
    /// </summary>
    public static RegraRelevancia DeLegado(RelevanciaTipo tipo, IReadOnlyList<string> valores, string? privacyStatusFiltro)
    {
        RegraRelevancia baseRegra = tipo switch
        {
            RelevanciaTipo.LiveUnlisted => new RegraTodas([
                new CondicaoTexto(CampoRelevancia.Tipo, OperadorRelevancia.Igual, ["live"]),
                new CondicaoTexto(CampoRelevancia.Privacidade, OperadorRelevancia.Igual, ["unlisted"])]),
            RelevanciaTipo.TituloContem => new RegraAlguma(valores
                .Select(v => v.EndsWith('*')
                    ? new CondicaoTexto(CampoRelevancia.Titulo, OperadorRelevancia.ComecaCom, [v[..^1]])
                    : new CondicaoTexto(CampoRelevancia.Titulo, OperadorRelevancia.Contem, [v]))
                .Cast<RegraRelevancia>()
                .ToList()),
            _ => throw new InvalidOperationException($"RelevanciaTipo não suportado: {tipo}")
        };

        return privacyStatusFiltro is null
            ? baseRegra
            : new RegraTodas([baseRegra, new CondicaoTexto(CampoRelevancia.Privacidade, OperadorRelevancia.Igual, [privacyStatusFiltro])]);
    }
}

/// <summary>Grupo E: todas as regras filhas precisam valer (grupo vazio vale true).</summary>
public sealed record RegraTodas(IReadOnlyList<RegraRelevancia> Regras) : RegraRelevancia
{
    public override bool Avalia(Video video) => Regras.All(r => r.Avalia(video));
}

/// <summary>Grupo OU: basta uma regra filha valer (grupo vazio vale false, como o antigo "nenhum valor casa").</summary>
public sealed record RegraAlguma(IReadOnlyList<RegraRelevancia> Regras) : RegraRelevancia
{
    public override bool Avalia(Video video) => Regras.Any(r => r.Avalia(video));
}

/// <summary>Negação da regra filha.</summary>
public sealed record RegraNao(RegraRelevancia Regra) : RegraRelevancia
{
    public override bool Avalia(Video video) => !Regra.Avalia(video);
}

/// <summary>
/// Condição sobre texto: título, descrição, privacidade, tipo ou transmissão. Comparação sem diferenciar
/// maiúsculas/minúsculas. <c>Em</c> e <c>Igual</c> casam se qualquer valor bater.
/// </summary>
public sealed record CondicaoTexto(CampoRelevancia Campo, OperadorRelevancia Operador, IReadOnlyList<string> Valores) : RegraRelevancia
{
    public override bool Avalia(Video video)
    {
        var texto = TextoDe(video);
        return Operador switch
        {
            OperadorRelevancia.Contem => Valores.Any(v => texto.Contains(v, StringComparison.OrdinalIgnoreCase)),
            OperadorRelevancia.ComecaCom => Valores.Any(v => texto.StartsWith(v, StringComparison.OrdinalIgnoreCase)),
            OperadorRelevancia.TerminaCom => Valores.Any(v => texto.EndsWith(v, StringComparison.OrdinalIgnoreCase)),
            OperadorRelevancia.Igual or OperadorRelevancia.Em => Valores.Any(v => string.Equals(texto, v, StringComparison.OrdinalIgnoreCase)),
            _ => false
        };
    }

    private string TextoDe(Video video) => Campo switch
    {
        CampoRelevancia.Titulo => video.Title ?? "",
        CampoRelevancia.Descricao => video.Description ?? "",
        CampoRelevancia.Privacidade => video.PrivacyStatus ?? "",
        CampoRelevancia.Tipo => video.Type switch
        {
            VideoType.Live => "live",
            VideoType.Short => "short",
            VideoType.Premiere => "premiere",
            _ => "upload"
        },
        CampoRelevancia.Transmissao => video.Transmissao switch
        {
            Transmissao.Agendada => "agendada",
            Transmissao.AoVivo => "ao_vivo",
            Transmissao.Encerrada => "encerrada",
            _ => "nenhuma"
        },
        _ => ""
    };
}

/// <summary>
/// Condição sobre a duração em segundos. Duração desconhecida (Length vazio ou ilegível) nunca casa,
/// nem em "menor que": sem a informação, a regra não decide pelo vídeo.
/// </summary>
public sealed record CondicaoNumero(CampoRelevancia Campo, OperadorRelevancia Operador, IReadOnlyList<double> Valores) : RegraRelevancia
{
    public override bool Avalia(Video video)
    {
        if (!TimeSpan.TryParse(video.Length, CultureInfo.InvariantCulture, out var duracao) || string.IsNullOrWhiteSpace(video.Length))
            return false;

        var s = duracao.TotalSeconds;
        return Operador switch
        {
            OperadorRelevancia.Igual => s == Valores[0],
            OperadorRelevancia.Maior => s > Valores[0],
            OperadorRelevancia.MaiorIgual => s >= Valores[0],
            OperadorRelevancia.Menor => s < Valores[0],
            OperadorRelevancia.MenorIgual => s <= Valores[0],
            OperadorRelevancia.Entre => s >= Valores[0] && s <= Valores[1],
            _ => false
        };
    }
}

/// <summary>
/// Condição sobre a data de envio, comparada por dia (a hora é ignorada). <c>Antes</c> = dia estritamente
/// anterior; <c>Depois</c> = dia estritamente posterior; <c>Entre</c> = do primeiro ao segundo dia,
/// inclusive. Data de envio desconhecida nunca casa.
/// </summary>
public sealed record CondicaoData(CampoRelevancia Campo, OperadorRelevancia Operador, IReadOnlyList<DateTime> Valores) : RegraRelevancia
{
    public override bool Avalia(Video video)
    {
        if (video.UploadedAt is not { } enviado) return false;

        var dia = enviado.Date;
        return Operador switch
        {
            OperadorRelevancia.Antes => dia < Valores[0].Date,
            OperadorRelevancia.Depois => dia > Valores[0].Date,
            OperadorRelevancia.Entre => dia >= Valores[0].Date && dia <= Valores[1].Date,
            _ => false
        };
    }
}
