using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Infrastructure.Precos;

/// <summary>
/// Substitui o antigo PricingCalculator estático (Task #130) — mesma lógica de cálculo, preço
/// agora vem de IPricingRepository (tabela provedor_preco) em vez de dicionário em código.
/// </summary>
public sealed class PricingService(IPricingRepository repository, ILogger<PricingService> logger) : IPricingService
{
    /// <summary>
    /// Retorna null se provedor/modelo vazio, não cadastrado, OU se a consulta ao repositório
    /// falhar (rede, Supabase fora do ar, tabela ainda não migrada) — diferente do antigo
    /// PricingCalculator (dicionário puro em memória, nunca podia falhar), a versão orientada a
    /// banco introduz uma nova classe de erro que não pode vazar: telemetria de custo é
    /// best-effort, nunca deveria derrubar resumo/categorização/embedding porque o preço não foi
    /// encontrado. "Melhor não estimar do que estimar errado" — e "melhor não estimar do que
    /// quebrar a etapa".
    /// </summary>
    public async Task<decimal?> CalcularAsync(string provedor, string? modelo, int? tokensEntrada, int? tokensSaida = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(provedor) || string.IsNullOrWhiteSpace(modelo))
            return null;

        Domain.ValueObjects.Preco? preco;
        try
        {
            preco = await repository.ObterAsync(provedor, modelo, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Falha ao consultar preço de {Provedor}/{Modelo} — custoEstimadoUsd sai null nesta chamada", provedor, modelo);
            return null;
        }

        if (preco is null)
            return null;

        var custoEntrada = (tokensEntrada ?? 0) * preco.EntradaPorMilhao / 1_000_000m;
        var custoSaida = (tokensSaida ?? 0) * preco.SaidaPorMilhao / 1_000_000m;
        return custoEntrada + custoSaida;
    }

    /// <summary>Mesma regra "melhor não estimar do que estimar errado": null se segundos/preço ausente ou a consulta falhar.</summary>
    public async Task<decimal?> CalcularPorHoraAsync(string provedor, string? modelo, double? segundos, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(provedor) || string.IsNullOrWhiteSpace(modelo) || segundos is null)
            return null;

        Domain.ValueObjects.Preco? preco;
        try
        {
            preco = await repository.ObterAsync(provedor, modelo, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Falha ao consultar preço por hora de {Provedor}/{Modelo} — custoEstimadoUsd sai null nesta chamada", provedor, modelo);
            return null;
        }

        return preco?.PorHora is { } porHora ? (decimal)(segundos.Value / 3600.0) * porHora : null;
    }
}
