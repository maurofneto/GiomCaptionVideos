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
/// Campos, operadores e valores permitidos vêm de <see cref="CatalogoRelevancia"/>. Valor: texto, número
/// ou data "yyyy-MM-dd"; "em" recebe lista; "entre" recebe [de, até]. Valida tudo ao carregar, pra uma
/// regra errada falhar aqui em vez de errar vídeo a vídeo. As chaves podem vir em qualquer ordem (o jsonb
/// do Postgres não preserva a ordem).
/// </summary>
public static class RegraRelevanciaParser
{
    private const int ProfundidadeMaxima = 8;
    private const int NosMaximos = 100;

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

        var campo = CatalogoRelevancia.ObterCampo(nomeCampo)
            ?? throw new RegraRelevanciaInvalidaException(
                $"{caminho}: campo desconhecido \"{nomeCampo}\" (use {string.Join(", ", CatalogoRelevancia.Campos.Select(c => c.Nome))}).");

        var operador = CatalogoRelevancia.ObterOperador(nomeOp)
            ?? throw new RegraRelevanciaInvalidaException($"{caminho}: operador desconhecido \"{nomeOp}\".");
        if (campo.Operadores.All(o => o.Operador != operador.Operador))
            throw new RegraRelevanciaInvalidaException($"{caminho}: o campo \"{nomeCampo}\" não aceita o operador \"{nomeOp}\".");

        var valor = el.GetProperty("valor");
        var pathValor = caminho + ".valor";
        var op = operador.Operador;

        return campo.Tipo switch
        {
            TipoValorRelevancia.Numero => new CondicaoNumero(campo.Campo, op, LerNumeros(valor, op, pathValor)),
            TipoValorRelevancia.Data => new CondicaoData(campo.Campo, op, LerDatas(valor, op, pathValor)),
            TipoValorRelevancia.Lista => new CondicaoTexto(campo.Campo, op, LerValoresDaLista(campo, LerTextos(valor, op, pathValor), pathValor)),
            _ => new CondicaoTexto(campo.Campo, op, LerTextos(valor, op, pathValor))
        };
    }

    private static List<string> LerValoresDaLista(CampoCatalogo campo, List<string> textos, string caminho)
    {
        var normalizados = textos.Select(t => t.ToLowerInvariant()).ToList();
        var invalido = normalizados.FirstOrDefault(t => !campo.ValoresPermitidos.Contains(t));
        if (invalido is not null)
            throw new RegraRelevanciaInvalidaException(
                $"{caminho}: \"{invalido}\" não é valor de \"{campo.Nome}\" (use {string.Join(", ", campo.ValoresPermitidos)}).");
        return normalizados;
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
