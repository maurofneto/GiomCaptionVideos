using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Options;

namespace Giom.VideoManager.Infrastructure.GuardRails;

public class AllowlistControleAcessoOptions
{
    public string[] ChatIdsAutorizados { get; init; } = [];
}

/// <summary>
/// v1 de IControleAcessoService: lista fixa configurada no appsettings — funciona bem pra um
/// número pequeno e conhecido de alunos. Trocar por uma lista persistida (self-service com
/// código de convite) no futuro é só implementar IControleAcessoService de novo — o guardrail
/// que consome isso (AcessoRestritoGuardRail) não muda nada.
/// </summary>
public class AllowlistControleAcessoService(IOptions<AllowlistControleAcessoOptions> options) : IControleAcessoService
{
    public Task<bool> EstaAutorizadoAsync(string chatId, CancellationToken ct = default)
        => Task.FromResult(options.Value.ChatIdsAutorizados.Contains(chatId));
}
