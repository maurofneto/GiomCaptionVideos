using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace Giom.VideoManager.Infrastructure.Anthropic;

public class ClaudeResumoOptions
{
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = "claude-haiku-4-5-20251001";

    // O prompt pede ~1200-1500 palavras (resumo.md) — em português isso passa fácil de
    // 2048 tokens; deixado com folga pra não cortar o resumo no meio.
    public int MaxTokens { get; init; } = 4096;
}

/// <summary>
/// Gera resumo executivo usando Claude — modelo configurável via ClaudeResumo:Model,
/// independente do modelo usado pela categorização ou pelo bot.
/// System prompt carregado de Prompts/resumo.md e cacheado via PromptCaching.
/// Usa Transcricao.ConteudoLimpo (sem timestamps) como entrada.
/// </summary>
public class ClaudeResumoService : IResumoService
{
    private readonly AnthropicClient _client;
    private readonly ClaudeResumoOptions _options;
    private readonly ILogger<ClaudeResumoService> _logger;
    private readonly ITelemetriaService _telemetriaService;
    private readonly string _systemPrompt;

    public ClaudeResumoService(IOptions<ClaudeResumoOptions> options, PromptLoader prompts, IHttpClientFactory httpClientFactory, ILogger<ClaudeResumoService> logger, ITelemetriaService telemetriaService)
    {
        _options = options.Value;
        _logger = logger;
        _telemetriaService = telemetriaService;
        _client = new AnthropicClient(_options.ApiKey, httpClientFactory.CreateClient("anthropic"));
        _systemPrompt = prompts.Load("resumo.md");
    }

    public async Task<Resumo?> GerarResumoAsync(Video video, CancellationToken ct = default)
    {
        var textoLimpo = video.Transcricao?.ConteudoLimpo;
        if (string.IsNullOrWhiteSpace(textoLimpo))
            return null;

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await _client.Messages.GetClaudeMessageAsync(new MessageParameters
            {
                Model         = _options.Model,
                MaxTokens     = _options.MaxTokens,
                PromptCaching = PromptCacheType.AutomaticToolsAndSystem,
                System        = [new SystemMessage(_systemPrompt)],
                Messages      = [new Message { Role = RoleType.User, Content = [new TextContent { Text = textoLimpo }] }]
            }, ct);

            // Concatena todos os blocos de texto — FirstOrDefault() descartava o resto
            // quando a resposta vinha em mais de um bloco.
            var content = string.Concat(response.Content.OfType<TextContent>().Select(c => c.Text));
            if (string.IsNullOrWhiteSpace(content))
                return null;

            if (response.StopReason == "max_tokens")
                _logger.LogWarning("Resumo cortado por MaxTokens ({MaxTokens}) para {VideoId}", _options.MaxTokens, video.Id);

            _logger.LogInformation("Resumo gerado para {VideoId}", video.Id);

            await _telemetriaService.RegistrarAsync(new RegistroTelemetria(
                Tipo: "resumo",
                EntidadeId: video.Id,
                TempoMs: stopwatch.ElapsedMilliseconds,
                Sucesso: true,
                Provedor: "Claude",
                Modelo: _options.Model,
                TokensEntrada: response.Usage?.InputTokens,
                TokensSaida: response.Usage?.OutputTokens,
                Detalhes: new { stopReason = response.StopReason }
            ), ct);

            return new Resumo
            {
                VideoId   = video.Id,
                Descricao = content,
                Origem    = "Claude",
                GeradoEm  = DateTime.UtcNow,
                Capitulos = []
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao gerar resumo via Claude para vídeo {VideoId}", video.Id);

            await _telemetriaService.RegistrarAsync(new RegistroTelemetria(
                Tipo: "resumo",
                EntidadeId: video.Id,
                TempoMs: stopwatch.ElapsedMilliseconds,
                Sucesso: false,
                Provedor: "Claude",
                Modelo: _options.Model
            ), ct);

            throw;
        }
    }
}
