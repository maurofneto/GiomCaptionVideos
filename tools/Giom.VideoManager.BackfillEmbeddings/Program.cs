using Giom.VideoManager.Composition;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Infrastructure;
using Giom.VideoManager.Infrastructure.Logging;
using Giom.VideoManager.Infrastructure.Sentry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

// US #118 — backfill do corpus existente com os provedores de embedding que faltam (Voyage/
// Cohere; ver Feature #115/appsettings.json: FallbackChain propositalmente sem OpenAI, já
// indexado). Roda uma vez, fora do fluxo normal de fila/etapa — não é um worker de produção,
// é uma ferramenta de operação (mesma ideia do SmokeTests: composição real, execução única).
// Escopo dos vídeos elegíveis: quem já tem chunk da origem-base (o "corpus existente" que a US
// pede pra reindexar) — não é "todo vídeo com transcrição", que incluiria vídeos nunca
// processados por outro motivo (fora do escopo aqui). A ferramenta em si não sabe nada de
// Supabase/Postgres — só fala com IVideoRepository/IVideoChunkRepository/IVideoIndexingService
// (Domain), então funciona igual com qualquer Persistence:Provider que os implemente.
//
// --limite N: processa só os N primeiros vídeos elegíveis — usar numa primeira validação antes
// de rodar o corpus inteiro (custo real de API, rate limit real).
// --origem-base NOME: origem cujo corpus já indexado define os vídeos elegíveis (default
// "OpenAI") — deixa a ferramenta reutilizável se um dia o backfill precisar partir de outro
// provedor já indexado, sem tocar código.

var limite = ObterArgInt(args, "--limite");
var origemBase = ObterArgString(args, "--origem-base") ?? "OpenAI";

using var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((_, config) =>
    {
        config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        config.AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Development"}.json", optional: true);
        config.AddEnvironmentVariables();
        config.AddSecretsProvider(config.Build());
    })
    .UseSerilog((context, _, configuration) =>
    {
        configuration
            .ReadFrom.Configuration(context.Configuration)
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithThreadId()
            .Enrich.With<ShortSourceContextEnricher>()
            .Enrich.With<SessionContextEnricher>()
            .AddSentrySink(context.Configuration);
    })
    .ConfigureServices((ctx, services) =>
    {
        var config = ctx.Configuration;

        services.AddPersistence(config);      // IVideoRepository real — só leitura aqui (GetByIdAsync)
        services.AddEmbeddingService(config); // IVideoIndexingService com a cadeia de Embedding:FallbackChain
        services.AddSessionContext();
        services.AddTelemetriaService(config); // custo/tempo por vídeo indo pro interacao_log, igual produção
        services.AddTracingService(config);    // Sentry Performance — span "embedding" por vídeo
    })
    .Build();

await host.StartAsync();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
var videoRepository = host.Services.GetRequiredService<IVideoRepository>();
var videoChunkRepository = host.Services.GetRequiredService<IVideoChunkRepository>();
var indexingService = host.Services.GetRequiredService<IVideoIndexingService>();
var sessionContext = host.Services.GetRequiredService<ISessionContext>();

var videoIds = (await videoChunkRepository.ListarVideoIdsPorOrigemAsync(origemBase)).ToList();
if (limite is int n) videoIds = videoIds.Take(n).ToList();

logger.LogInformation("Backfill (US #118) — {Total} vídeos elegíveis (origem {OrigemBase} já indexada){LimiteInfo}",
    videoIds.Count, origemBase, limite is int l ? $", limitado a {l} nesta run" : "");

var indexados = 0;
var pulados = 0;
var falhas = 0;

foreach (var videoId in videoIds)
{
    var video = await videoRepository.GetByIdAsync(videoId);
    if (video?.Transcricao is null)
    {
        logger.LogWarning("Vídeo {VideoId} sem transcrição carregada — pulando", videoId);
        pulados++;
        continue;
    }

    sessionContext.Definir(new SessaoObservabilidade(
        SessionId: Guid.NewGuid().ToString(),
        SessionName: "backfill-us118",
        SessionPath: $"/backfill-embeddings/{video.ChannelTitle}/{video.Id}",
        UserId: video.ChannelTitle,
        Propriedades: new Dictionary<string, string> { ["VideoId"] = video.Id, ["VideoTitle"] = video.Title }));

    try
    {
        await indexingService.IndexarAsync(video);
        logger.LogInformation("Backfill — indexado: {VideoId} — {Title}", video.Id, video.Title);
        indexados++;
    }
    catch (Exception ex)
    {
        // PipelineEmbeddingService já loga e segue pro próximo PROVEDOR internamente (não
        // lança) — chegar uma exceção até aqui é algo fora desse tratamento (ex.: farol/DB).
        // Não aborta o backfill inteiro por um vídeo — só contabiliza e segue.
        logger.LogError(ex, "Backfill — falha inesperada em {VideoId}", video.Id);
        falhas++;
    }

    await Task.Delay(500);
}

logger.LogInformation("Backfill concluído — {Indexados} indexados, {Pulados} pulados, {Falhas} falhas de {Total} elegíveis",
    indexados, pulados, falhas, videoIds.Count);

await host.StopAsync();
await Log.CloseAndFlushAsync();

static int? ObterArgInt(string[] args, string nome)
{
    var idx = Array.IndexOf(args, nome);
    return idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], out var valor) ? valor : null;
}

static string? ObterArgString(string[] args, string nome)
{
    var idx = Array.IndexOf(args, nome);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}
