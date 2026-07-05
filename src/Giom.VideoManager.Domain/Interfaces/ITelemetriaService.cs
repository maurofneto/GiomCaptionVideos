namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>
/// Registro genérico de auditoria/observabilidade para qualquer operação de IA (resumo,
/// categorização, resposta do bot, embedding...). Deliberadamente desacoplado das entidades
/// de domínio (Resumo, CategoriaTeologica) — nem todo provedor (ex: SummarizeTech) tem noção
/// de "modelo" ou "tokens", então essa metadata nunca deveria virar coluna numa tabela de domínio.
/// </summary>
public interface ITelemetriaService
{
    Task RegistrarAsync(RegistroTelemetria registro, CancellationToken ct = default);
}

/// <summary>Um registro de auditoria de uma operação de IA. Campos nullable são preenchidos só por provedores que têm essa noção.</summary>
public record RegistroTelemetria(
    string Tipo,
    string EntidadeId,
    long TempoMs,
    bool Sucesso,
    string? Provedor = null,
    string? Modelo = null,
    int? TokensEntrada = null,
    int? TokensSaida = null,
    decimal? CustoEstimadoUsd = null,
    object? Detalhes = null);
