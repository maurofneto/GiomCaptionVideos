namespace Giom.VideoManager.Infrastructure.Canais;

/// <summary>
/// Corta um texto longo em pedaços que cabem no limite de tamanho de uma mensagem do canal
/// (Telegram: 4096 caracteres; WhatsApp: 4096 no corpo do texto) — cada canal passa o seu
/// limite. Prefere cortar em quebra de parágrafo (linha em branco); só corta no meio de um
/// parágrafo quando ele sozinho já é maior que o limite.
///
/// Vive no kernel Infrastructure (e não dentro de um adapter) porque é a MESMA regra pros
/// dois canais — Infrastructure.Telegram e Infrastructure.WhatsApp não podem se referenciar
/// entre si (ver CLAUDE.md), então duplicar seria a única alternativa.
/// </summary>
public static class DivisorDeMensagem
{
    public static List<string> DividirEmPartes(string texto, int limite)
    {
        if (texto.Length <= limite) return [texto];

        var partes = new List<string>();
        var atual = new System.Text.StringBuilder();

        foreach (var paragrafo in texto.Split("\n\n"))
        {
            var candidato = atual.Length == 0 ? paragrafo : $"{atual}\n\n{paragrafo}";

            if (candidato.Length > limite && atual.Length > 0)
            {
                partes.Add(atual.ToString());
                atual.Clear();
                atual.Append(paragrafo);
            }
            else
            {
                atual.Clear();
                atual.Append(candidato);
            }

            while (atual.Length > limite)
            {
                partes.Add(atual.ToString(0, limite));
                atual.Remove(0, limite);
            }
        }

        if (atual.Length > 0) partes.Add(atual.ToString());
        return partes;
    }
}
