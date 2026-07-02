using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Domain.Entities;
using Domain.Enums;
using Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Infrastructure.Categorization;

public class ClaudeCategorizacaoOptions
{
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = "claude-haiku-4-5-20251001";
}

/// <summary>
/// Classifica o resumo em até 3 áreas da Teologia Sistemática usando Claude.
/// Retorna uma lista ordenada por peso desc — a primeira é a área principal.
/// </summary>
public class ClaudeCategorizacaoService : ICategorizacaoService
{
    private readonly AnthropicClient _client;
    private readonly ClaudeCategorizacaoOptions _options;
    private readonly ILogger<ClaudeCategorizacaoService> _logger;

    private static readonly string Prompt =
        """
        Classifique o resumo da aula teológica abaixo nas áreas da Teologia Sistemática relevantes.

        Áreas disponíveis (use o nome exato):
        Prolegomenos, TeologiaPropria, Angelologia, AntropologiaTeologica, Cristologia,
        Pneumatologia, Soteriologia, Eclesiologia, Sacramentologia, Escatologia, EticaCrista, Missiologia

        Retorne entre 1 e 3 áreas que melhor descrevem o conteúdo.
        Para cada área, atribua um peso de 0.01 a 1.00 indicando sua relevância para esta aula.
        A soma dos pesos não precisa ser 1 — cada peso é independente.

        Responda SOMENTE em JSON válido, sem markdown:
        [
          {"area": "<nome exato>", "subcategoria": "<tema específico>", "peso": <0.01 a 1.00>},
          ...
        ]

        RESUMO:
        {resumo}
        """;

    public ClaudeCategorizacaoService(IOptions<ClaudeCategorizacaoOptions> options, ILogger<ClaudeCategorizacaoService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _client = new AnthropicClient(_options.ApiKey);
    }

    public async Task<IReadOnlyList<CategoriaTeologica>> ClassificarAsync(Video video, CancellationToken ct = default)
    {
        var prompt = Prompt.Replace("{resumo}", video.Resumo?.Descricao ?? string.Empty);

        try
        {
            var response = await _client.Messages.GetClaudeMessageAsync(new MessageParameters
            {
                Model = _options.Model,
                MaxTokens = 512,
                Messages = [new Message { Role = RoleType.User, Content = [new TextContent { Text = prompt }] }]
            }, ct);

            var json = response.Content.OfType<TextContent>().FirstOrDefault()?.Text;
            if (string.IsNullOrWhiteSpace(json)) return [];

            json = json.Replace("\n", "").Replace("\r", "").Replace("```json", "").Replace("```", "").Trim();

            var items = JsonConvert.DeserializeAnonymousType(json,
                new[] { new { area = "", subcategoria = "", peso = 0.0 } });

            if (items == null || items.Length == 0) return [];

            var categorias = new List<CategoriaTeologica>();
            foreach (var item in items)
            {
                if (!Enum.TryParse<AreaTeologica>(item.area, ignoreCase: true, out var area))
                {
                    _logger.LogWarning("Área teológica desconhecida: {Area}", item.area);
                    continue;
                }

                categorias.Add(new CategoriaTeologica
                {
                    VideoId    = video.Id,
                    Area       = area,
                    Subcategoria = item.subcategoria ?? string.Empty,
                    Peso       = (decimal)Math.Clamp(item.peso, 0.01, 1.0),
                    ClassificadoEm = DateTime.UtcNow
                });
            }

            // Ordena por peso desc — a principal fica em primeiro
            return categorias.OrderByDescending(c => c.Peso).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao classificar vídeo {VideoId}", video.Id);
            throw;
        }
    }
}
