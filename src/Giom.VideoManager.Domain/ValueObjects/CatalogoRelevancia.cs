namespace Giom.VideoManager.Domain.ValueObjects;

/// <summary>Forma do valor que um campo aceita numa condição.</summary>
public enum TipoValorRelevancia
{
    /// <summary>Texto livre (título, descrição).</summary>
    Texto,

    /// <summary>Um dos valores de uma lista fechada (privacidade, tipo, transmissão).</summary>
    Lista,

    /// <summary>Número maior ou igual a zero (duração em segundos).</summary>
    Numero,

    /// <summary>Data no formato yyyy-MM-dd (data de envio).</summary>
    Data
}

/// <summary>Um operador do JSON da regra (<c>Nome</c>) e como ele aparece para quem edita (<c>Rotulo</c>).</summary>
public sealed record OperadorCatalogo(string Nome, OperadorRelevancia Operador, string Rotulo);

/// <summary>
/// Um campo do vídeo que a regra pode examinar: o nome no JSON, o rótulo para quem edita, o tipo de valor,
/// os operadores que aceita e, nas listas fechadas, os valores permitidos.
/// </summary>
public sealed record CampoCatalogo(
    string Nome,
    CampoRelevancia Campo,
    string Rotulo,
    TipoValorRelevancia Tipo,
    IReadOnlyList<OperadorCatalogo> Operadores,
    IReadOnlyList<string> ValoresPermitidos);

/// <summary>
/// Catálogo único do que uma regra de relevância pode dizer: campos, operadores e valores permitidos. O
/// <see cref="RegraRelevanciaParser"/> valida contra ele, e uma futura API/Admin o expõe como está — a lista
/// existe só aqui, em código, sem arquivo paralelo pra manter em sincronia.
/// </summary>
public static class CatalogoRelevancia
{
    private static readonly OperadorCatalogo Contem = new("contem", OperadorRelevancia.Contem, "contém");
    private static readonly OperadorCatalogo ComecaCom = new("comeca_com", OperadorRelevancia.ComecaCom, "começa com");
    private static readonly OperadorCatalogo TerminaCom = new("termina_com", OperadorRelevancia.TerminaCom, "termina com");
    private static readonly OperadorCatalogo Igual = new("igual", OperadorRelevancia.Igual, "é igual a");
    private static readonly OperadorCatalogo Em = new("em", OperadorRelevancia.Em, "é um destes");
    private static readonly OperadorCatalogo Maior = new("maior", OperadorRelevancia.Maior, "maior que");
    private static readonly OperadorCatalogo MaiorIgual = new("maior_igual", OperadorRelevancia.MaiorIgual, "maior ou igual a");
    private static readonly OperadorCatalogo Menor = new("menor", OperadorRelevancia.Menor, "menor que");
    private static readonly OperadorCatalogo MenorIgual = new("menor_igual", OperadorRelevancia.MenorIgual, "menor ou igual a");
    private static readonly OperadorCatalogo Entre = new("entre", OperadorRelevancia.Entre, "entre");
    private static readonly OperadorCatalogo Antes = new("antes", OperadorRelevancia.Antes, "antes de");
    private static readonly OperadorCatalogo Depois = new("depois", OperadorRelevancia.Depois, "depois de");

    private static readonly OperadorCatalogo[] OperadoresTexto = [Contem, ComecaCom, TerminaCom, Igual, Em];
    private static readonly OperadorCatalogo[] OperadoresLista = [Igual, Em];
    private static readonly OperadorCatalogo[] OperadoresNumero = [Igual, Maior, MaiorIgual, Menor, MenorIgual, Entre];
    private static readonly OperadorCatalogo[] OperadoresData = [Antes, Depois, Entre];

    public static IReadOnlyList<CampoCatalogo> Campos { get; } =
    [
        new("titulo", CampoRelevancia.Titulo, "Título", TipoValorRelevancia.Texto, OperadoresTexto, []),
        new("descricao", CampoRelevancia.Descricao, "Descrição", TipoValorRelevancia.Texto, OperadoresTexto, []),
        new("privacidade", CampoRelevancia.Privacidade, "Privacidade", TipoValorRelevancia.Lista, OperadoresLista, ["public", "unlisted", "private"]),
        new("tipo", CampoRelevancia.Tipo, "Tipo de vídeo", TipoValorRelevancia.Lista, OperadoresLista, ["upload", "live", "short", "premiere"]),
        new("transmissao", CampoRelevancia.Transmissao, "Transmissão ao vivo", TipoValorRelevancia.Lista, OperadoresLista, ["nenhuma", "agendada", "ao_vivo", "encerrada"]),
        new("duracao_segundos", CampoRelevancia.DuracaoSegundos, "Duração (segundos)", TipoValorRelevancia.Numero, OperadoresNumero, []),
        new("enviado_em", CampoRelevancia.EnviadoEm, "Data de envio", TipoValorRelevancia.Data, OperadoresData, [])
    ];

    public static CampoCatalogo? ObterCampo(string nome) => Campos.FirstOrDefault(c => c.Nome == nome);

    /// <summary>Operador pelo nome do JSON, ou null se não existir em nenhum campo.</summary>
    public static OperadorCatalogo? ObterOperador(string nome) =>
        Campos.SelectMany(c => c.Operadores).FirstOrDefault(o => o.Nome == nome);
}
