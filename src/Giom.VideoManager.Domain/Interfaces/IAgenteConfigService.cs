namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>Leitura da tabela agente_config (Config First) — configuração dos agentes do bot que vive no banco, não no appsettings.</summary>
public interface IAgenteConfigRepository
{
    /// <summary>Instrução de concisão do provedor; cai na linha '*' se não houver a do provedor. Vazio = nenhuma.</summary>
    Task<string> ObterInstrucaoConcisaoAsync(string provedor, CancellationToken ct = default);
}

/// <summary>
/// Configuração dos agentes do bot, com cache curto sobre <see cref="IAgenteConfigRepository"/>.
/// Nunca lança: se o banco estiver indisponível devolve o último valor conhecido (ou vazio) — o bot
/// deve responder, só sem a instrução de concisão, em vez de falhar.
/// </summary>
public interface IAgenteConfigService
{
    Task<string> ObterInstrucaoConcisaoAsync(string provedor, CancellationToken ct = default);
}
