using System.Globalization;
using System.Text.Json;
using Giom.VideoManager.Domain.Exceptions;

namespace Giom.VideoManager.Domain.ValueObjects;

/// <summary>
/// Interpreta o JSON de canal_ingestao.regra_relevancia. Formato:
/// <code>
/// { "todas":  [ nó, ... ] }                       // E (vazio = verdadeiro)
/// { "alguma": [ nó, ... ] }                       // OU (vazio = falso)
/// { "nao":    nó }                                // negação
/// { "campo": "titulo", "op": "comeca_com", "valor": "GIOM" }   // condição
/// </code>
/// Campos: titulo, descricao, privacidade, tipo, transmissao, duracao_segundos, enviado_em.
/// Operadores por campo — texto (titulo, descricao): contem, comeca_com, termina_com, igual, em;
/// listas fechadas (privacidade, tipo, transmissao): igual, em; duracao_segundos: igual, maior,
/// maior_igual, menor, menor_igual, entre; enviado_em: antes, depois, entre. Valor: texto, número
/// ou data "yyyy-MM-dd"; "em" recebe lista; "entre" recebe [de, até]. Valida tudo ao carregar, pra
/// uma regra errada falhar aqui (e cair na regra anterior) em vez de errar vídeo a vídeo.
/// </summary>
public static class RegraRelevanciaParser
{
    private const int ProfundidadeMaxima = 8;
    private const int NosMaximos = 100;

    private static readonly Dictionary<string, CampoRelevancia> Campos = new()
    {
        ["titulo"] = CampoRelevancia.Titulo,
        ["descricao"] = CampoRelevancia.Descricao,
        ["privacidade"] = CampoRelevancia.Privacidade,
        ["tipo"] = CampoRelevancia.Tipo,
        ["transmissao"] = CampoRelevancia.Transmissao,
        ["duracao_segundos"] = CampoRelevancia.DuracaoSegundos,
        ["enviado_em"] = CampoRelevancia.EnviadoEm
    };

    private static readonly Dictionary<string, OperadorRelevancia> Operadores = new()
    {
        ["contem"] = OperadorRelevancia.Contem,
        ["comeca_com"] = OperadorRelevancia.ComecaCom,
        ["termina_com"] = OperadorRelevancia.TerminaCom,
        ["igual"] = OperadorRelevancia.Igual,
        ["em"] = OperadorRelevancia.Em,
        ["maior"] = OperadorRelevancia.Maior,
        ["maior_igual"] = OperadorRelevancia.MaiorIgual,
        ["menor"] = OperadorRelevancia.Menor,
        ["menor_igual"] = OperadorRelevancia.MenorIgual,
        ["entre"] = OperadorRelevancia.Entre,
        ["antes"] = OperadorRelevancia.Antes,
        ["depois"] = OperadorRelevancia.Depois
    };

    private static readonly OperadorRelevancia[] OperadoresTexto =
        [OperadorRelevancia.Contem, OperadorRelevancia.ComecaCom, OperadorRelevancia.TerminaCom, OperadorRelevancia.Igual, OperadorRelevancia.Em];
    private static readonly OperadorRelevancia[] OperadoresLista = [OperadorRelevancia.Igual, OperadorRelevancia.Em];
    private static readonly OperadorRelevancia[] OperadoresNumero =
        [OperadorRelevancia.Igual, OperadorRelevancia.Maior, OperadorRelevancia.MaiorIgual, OperadorRelevancia.Menor, OperadorRelevancia.MenorIgual, OperadorRelevancia.Entre];
    private static readonly OperadorRelevancia[] OperadoresData = [OperadorRelevancia.Antes, OperadorRelevancia.Depois, OperadorRelevancia.Entre];

    private static readonly Dictionary<CampoRelevancia, string[]> ValoresPermitidos = new()
    {
        [CampoRelevancia.Privacidade] = ["public", "unlisted", "private"],
        [CampoRelevancia.Tipo] = ["upload", "live", "short", "premiere"],
        [CampoRelevancia.Transmissao] = ["nenhuma", "agendada", "ao_vivo", "encerrada"]
    };

    public static RegraRelevancia Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new RegraRelevanciaInvalidaException("A regra está vazia.");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException e)
        {
            throw new RegraRelevanciaInvalidaException($"JSON inválido: {e.Message}");
        }

        using (doc)
        {
            var nos = 0;
            return LerNo(doc.RootElement, 1, ref nos, "$");
        }
    }

    private static RegraRelevancia LerNo(JsonElement el, int profundidade, ref int nos, string caminho)
    {
        if (el.ValueKind != JsonValueKind.Object)
            throw new RegraRelevanciaInvalidaException($"{caminho}: esperava um objeto.");
        if (profundidade > ProfundidadeMaxima)
            throw new RegraRelevanciaInvalidaException($"{caminho}: aninhamento acima de {ProfundidadeMaxima} níveis.");
        if (++nos > NosMaximos)
            throw new RegraRelevanciaInvalidaException($"A regra passa de {NosMaximos} nós.");

        var chaves = el.EnumerateObject().Select(p => p.Name).ToList();

        if (chaves.Contains("campo"))
            return LerCondicao(el, chaves, caminho);

        if (chaves.Count != 1)
            throw new RegraRelevanciaInvalidaException($"{caminho}: um grupo tem exatamente uma chave (todas, alguma ou nao); veio [{string.Join(", ", chaves)}].");

        switch (chaves[0])
        {
            case "todas":
                return new RegraTodas(LerLista(el.GetProperty("todas"), profundidade, ref nos, caminho + ".todas"));
            case "alguma":
                return new RegraAlguma(LerLista(el.GetProperty("alguma"), profundidade, ref nos, caminho + ".alguma"));
            case "nao":
                return new RegraNao(LerNo(el.GetProperty("nao"), profundidade + 1, ref nos, caminho + ".nao"));
            default:
                throw new RegraRelevanciaInvalidaException($"{caminho}: chave desconhecida \"{chaves[0]}\".");
        }
    }

    private static List<RegraRelevancia> LerLista(JsonElement lista, int profundidade, ref int nos, string caminho)
    {
        if (lista.ValueKind != JsonValueKind.Array)
            throw new RegraRelevanciaInvalidaException($"{caminho}: esperava uma lista.");

        var resultado = new List<RegraRelevancia>();
        var i = 0;
        foreach (var filho in lista.EnumerateArray())
            resultado.Add(LerNo(filho, profundidade + 1, ref nos, $"{caminho}[{i++}]"));
        return resultado;
    }

    private static RegraRelevancia LerCondicao(JsonElement el, List<string> chaves, string caminho)
    {
        var extras = chaves.Where(c => c is not ("campo" or "op" or "valor")).ToList();
        if (extras.Count > 0)
            throw new RegraRelevanciaInvalidaException($"{caminho}: chave desconhecida em condição: \"{extras[0]}\".");
        if (!chaves.Contains("op") || !chaves.Contains("valor"))
            throw new RegraRelevanciaInvalidaException($"{caminho}: uma condição precisa de campo, op e valor.");

        var nomeCampo = LerTexto(el.GetProperty("campo"), caminho + ".campo");
        var nomeOp = LerTexto(el.GetProperty("op"), caminho + ".op");

        if (!Campos.TryGetValue(nomeCampo, out var campo))
            throw new RegraRelevanciaInvalidaException($"{caminho}: campo desconhecido \"{nomeCampo}\" (use {string.Join(", ", Campos.Keys)}).");
        if (!Operadores.TryGetValue(nomeOp, out var op))
            throw new RegraRelevanciaInvalidaException($"{caminho}: operador desconhecido \"{nomeOp}\".");

        var permitidos = campo switch
        {
            CampoRelevancia.Titulo or CampoRelevancia.Descricao => OperadoresTexto,
            CampoRelevancia.DuracaoSegundos => OperadoresNumero,
            CampoRelevancia.EnviadoEm => OperadoresData,
            _ => OperadoresLista
        };
        if (!permitidos.Contains(op))
            throw new RegraRelevanciaInvalidaException($"{caminho}: o campo \"{nomeCampo}\" não aceita o operador \"{nomeOp}\".");

        var valor = el.GetProperty("valor");
        var pathValor = caminho + ".valor";

        switch (campo)
        {
            case CampoRelevancia.DuracaoSegundos:
                return new CondicaoNumero(campo, op, LerNumeros(valor, op, pathValor));
            case CampoRelevancia.EnviadoEm:
                return new CondicaoData(campo, op, LerDatas(valor, op, pathValor));
            default:
                var textos = LerTextos(valor, op, pathValor);
                if (ValoresPermitidos.TryGetValue(campo, out var validos))
                {
                    textos = textos.Select(t => t.ToLowerInvariant()).ToList();
                    var invalido = textos.FirstOrDefault(t => !validos.Contains(t));
                    if (invalido is not null)
                        throw new RegraRelevanciaInvalidaException($"{pathValor}: \"{invalido}\" não é valor de \"{nomeCampo}\" (use {string.Join(", ", validos)}).");
                }
                return new CondicaoTexto(campo, op, textos);
        }
    }

    private static string LerTexto(JsonElement el, string caminho) =>
        el.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(el.GetString())
            ? el.GetString()!
            : throw new RegraRelevanciaInvalidaException($"{caminho}: esperava um texto não vazio.");

    private static List<string> LerTextos(JsonElement el, OperadorRelevancia op, string caminho)
    {
        if (op == OperadorRelevancia.Em)
        {
            if (el.ValueKind != JsonValueKind.Array || el.GetArrayLength() == 0)
                throw new RegraRelevanciaInvalidaException($"{caminho}: \"em\" espera uma lista não vazia de textos.");
            return el.EnumerateArray().Select(x => LerTexto(x, caminho)).ToList();
        }

        return [LerTexto(el, caminho)];
    }

    private static List<double> LerNumeros(JsonElement el, OperadorRelevancia op, string caminho)
    {
        if (op == OperadorRelevancia.Entre)
        {
            var par = LerPar(el, caminho, x => LerNumero(x, caminho));
            if (par[0] > par[1]) throw new RegraRelevanciaInvalidaException($"{caminho}: em \"entre\" o primeiro valor não pode passar do segundo.");
            return par;
        }

        return [LerNumero(el, caminho)];
    }

    private static double LerNumero(JsonElement el, string caminho) =>
        el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var n) && n >= 0
            ? n
            : throw new RegraRelevanciaInvalidaException($"{caminho}: esperava um número maior ou igual a zero.");

    private static List<DateTime> LerDatas(JsonElement el, OperadorRelevancia op, string caminho)
    {
        if (op == OperadorRelevancia.Entre)
        {
            var par = LerPar(el, caminho, x => LerData(x, caminho));
            if (par[0] > par[1]) throw new RegraRelevanciaInvalidaException($"{caminho}: em \"entre\" a primeira data não pode passar da segunda.");
            return par;
        }

        return [LerData(el, caminho)];
    }

    private static DateTime LerData(JsonElement el, string caminho) =>
        el.ValueKind == JsonValueKind.String
        && DateTime.TryParseExact(el.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : throw new RegraRelevanciaInvalidaException($"{caminho}: esperava uma data no formato yyyy-MM-dd.");

    private static List<T> LerPar<T>(JsonElement el, string caminho, Func<JsonElement, T> ler)
    {
        if (el.ValueKind != JsonValueKind.Array || el.GetArrayLength() != 2)
            throw new RegraRelevanciaInvalidaException($"{caminho}: \"entre\" espera [de, até].");
        return el.EnumerateArray().Select(ler).ToList();
    }
}
