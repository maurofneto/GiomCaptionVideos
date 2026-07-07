namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>
/// Decide se um chat pode conversar com o bot — genérico o bastante pra trocar de allowlist
/// simples por config (v1) por uma lista persistida com auto-cadastro via código de convite
/// (v2) no futuro, sem afetar quem consome isso (ver AcessoRestritoGuardRail).
/// </summary>
public interface IControleAcessoService
{
    Task<bool> EstaAutorizadoAsync(string chatId, CancellationToken ct = default);
}
