using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Options;

namespace Giom.VideoManager.Infrastructure.GuardRails;

public class PalavrasBloqueadasOptions
{
    public string[] Palavras { get; init; } = [];
    public string MensagemBloqueio { get; init; } = "Não posso ajudar com isso por aqui.";
}

/// <summary>Segurança: bloqueia perguntas que contenham algum termo da lista configurada (temas fora do escopo do bot, ofensivos, etc.).</summary>
public class PalavrasBloqueadasGuardRail(IOptions<PalavrasBloqueadasOptions> options) : IGuardRail
{
    public string Nome => "PalavrasBloqueadas";
    public GuardRailFase Fase => GuardRailFase.Entrada;

    public Task<GuardRailResultado> AvaliarAsync(GuardRailContexto contexto, CancellationToken ct = default)
    {
        var opcoes = options.Value;
        var termo = opcoes.Palavras.FirstOrDefault(p => contexto.Pergunta.Contains(p, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(termo is null
            ? GuardRailResultado.Ok
            : GuardRailResultado.Bloquear(opcoes.MensagemBloqueio, $"termo bloqueado: {termo}"));
    }
}
