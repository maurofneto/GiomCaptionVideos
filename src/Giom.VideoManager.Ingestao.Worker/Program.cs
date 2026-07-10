using Giom.VideoManager.Application;
using Giom.VideoManager.Application.Pipeline;
using Giom.VideoManager.Composition;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Infrastructure;
using Giom.VideoManager.Infrastructure.Logging;
using Giom.VideoManager.Infrastructure.Sentry;
using Giom.VideoManager.Infrastructure.YouTube;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

using var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((_, config) =>
    {
        config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        config.AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json", optional: true);
        config.AddEnvironmentVariables();
        // Secrets:Aws:Path aponta pra "/giom/{env}/canais/" — cobre tanto os secrets globais
        // (Sentry, Supabase) quanto os por-canal (instituto/Google/..., ministerio/Google/...),
        // já que o bulk-load é recursivo (ver AddIngestaoMultiCanal).
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

        // US #105 — worker único pra todos os canais ativos (Instituto/Ministério), lidos do
        // Supabase (canal_ingestao) no boot, em vez de um binário fixo por canal. Substitui
        // Instituto.Worker/Ministerio.Worker.
        services.AddYouTubeVideoSourceMultiCanal();
        services.AddActiveChannelContext();
        services.AddPersistence(config);
        services.AddTelemetriaService(config);
        services.AddTracingService(config);
        services.AddChannelProcessor(config);

        services.AddFilaVideoEtapa<VideoEtapaMensagem>("transcricao", config, "FilaTranscricao");

        // Lê canal_ingestao, registra credencial Google keyed por canal e o resolver de
        // IVideoHandler consumido por ChannelProcessor — precisa vir depois de
        // AddFilaVideoEtapa/AddTelemetriaService/AddTracingService só na ORDEM de execução da
        // run (o Func é resolvido preguiçosamente), não na ordem de registro aqui.
        services.AddIngestaoMultiCanal(config);
    })
    .Build();

await host.StartAsync();

var canais = host.Services.GetRequiredService<IReadOnlyList<CanalIngestaoConfig>>();
var channels = canais.Select(c => new ChannelConfig(
    c.ChannelId, c.PlaylistId, c.Nome, c.Mine,
    c.MaxVideosPerRun, c.DelayBetweenVideosMs, c.DelayBetweenPagesMs));

var processor = host.Services.GetRequiredService<ChannelProcessor>();
await processor.RunAsync(channels);

await host.StopAsync();
