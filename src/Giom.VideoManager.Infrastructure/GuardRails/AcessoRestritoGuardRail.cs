using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Options;

namespace Giom.VideoManager.Infrastructure.GuardRails;

public class AcessoRestritoOptions
{
    public string MensagemBloqueio { get; init; } = "Esse bot é de uso restrito — fala com quem administra pra liberar seu acesso.";
}

/// <summary>
/// Segurança: só deixa conversar quem IControleAcessoService autorizar (Board #22). Desligado
/// por padrão (não entra em GuardRails:Habilitados) — habilitar é decisão do Mauro quando/se
/// quiser restringir o bot a alunos matriculados.
/// </summary>
public class AcessoRestritoGuardRail(IControleAcessoService controleAcesso, IOptions<AcessoRestritoOptions> options) : IGuardRail
{
    public string Nome => "AcessoRestrito";
    public GuardRailFase Fase => GuardRailFase.Entrada;

    public async Task<GuardRailResultado> AvaliarAsync(GuardRailContexto contexto, CancellationToken ct = default)
    {
        var autorizado = await controleAcesso.EstaAutorizadoAsync(contexto.ChatId, ct);

        return autorizado
            ? GuardRailResultado.Ok
            : GuardRailResultado.Bloquear(options.Value.MensagemBloqueio, $"chatId {contexto.ChatId} não está na lista de autorizados");
    }
}
