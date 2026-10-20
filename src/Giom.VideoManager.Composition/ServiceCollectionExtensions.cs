using Giom.VideoManager.Application;
using Giom.VideoManager.Application.Handlers;
using Giom.VideoManager.Application.Pipeline;
using Giom.VideoManager.Application.Memoria;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Infrastructure;
using Giom.VideoManager.Infrastructure.Anthropic;
using Giom.VideoManager.Infrastructure.Cohere;
using Giom.VideoManager.Infrastructure.Feedback;
using Giom.VideoManager.Infrastructure.Grok;
using Giom.VideoManager.Infrastructure.GuardRails;
using Giom.VideoManager.Infrastructure.Helicone;
using Giom.VideoManager.Infrastructure.Kimi;
using Giom.VideoManager.Infrastructure.Observabilidade;
using Giom.VideoManager.Infrastructure.Precos;
using Giom.VideoManager.Infrastructure.Telemetria;
using Giom.VideoManager.Infrastructure.Redis;
using Giom.VideoManager.Infrastructure.Sentry;
using Giom.VideoManager.Infrastructure.Mongo;
using Giom.VideoManager.Infrastructure.OpenAI;
using Giom.VideoManager.Infrastructure.RabbitMq;
using Giom.VideoManager.Infrastructure.Resiliencia;
using Giom.VideoManager.Infrastructure.Sql;
using Giom.VideoManager.Infrastructure.Sqs;
using Giom.VideoManager.Infrastructure.Supabase;
using Giom.VideoManager.Infrastructure.SummarizeTech;
using Giom.VideoManager.Infrastructure.Telegram;
using Giom.VideoManager.Infrastructure.Voyage;
using Giom.VideoManager.Infrastructure.GrokVoice;
using Giom.VideoManager.Infrastructure.Whisper;
using Giom.VideoManager.Infrastructure.WhatsApp;
using Giom.VideoManager.Infrastructure.YouTube;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Giom.VideoManager.Composition;

/// <summary>
/// Ponto único de composição: cada método decide, a partir de config, qual adapter
/// concreto usar e delega ao ServiceCollectionExtensions do próprio projeto adapter —
/// esta classe não conhece detalhes de implementação de nenhum provider.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Persistence:Provider — Supabase (padrão), MongoDB, SqlServer.</summary>
    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration config)
    {
        var provider = config["Persistence:Provider"] ?? "Supabase";

        return provider switch
        {
            "MongoDB" => services.AddMongoPersistence(config),
            "SqlServer" => services.AddSqlPersistence(config),
            _ => services.AddSupabasePersistence(config)
        };
    }

    /// <summary>Config First: topologia do pipeline por canal vem da tabela etapa_topologia (Supabase).</summary>
    public static IServiceCollection AddTopologia(this IServiceCollection services, IConfiguration config) =>
        services.AddSupabaseTopologia(config);

    /// <summary>
    /// Transcrição — cadeia de fallback por canal (US #129). Cada adapter se registra via
    /// AddYouTubeCaption / AddYouTubeTranscript / AddYtDlpCaption / AddGrokVoiceTranscription /
    /// AddWhisperTranscription;
    /// FallbackCaptionService (único ICaptionService público) resolve a ordem via
    /// IEtapaProvedorConfigService pelo ChannelId. Credencial OAuth multi-canal:
    /// <see cref="AddTranscricaoMultiCanal"/> (US #146).
    /// </summary>
    public static IServiceCollection AddTranscriptionService(this IServiceCollection services, IConfiguration config)
    {
        services.AddEtapaProvedorConfigService(config);

        services.AddYouTubeCaption(config);
        services.AddYouTubeTranscript(config);
        services.AddYtDlpCaption(config);
        services.AddPricingService(config);   // custo por hora do GrokVoice vem de provedor_preco
        services.AddGrokVoiceTranscription(config);
        services.AddWhisperTranscription(config);

        services.AddSingleton<ICaptionService, FallbackCaptionService>();
        services.Decorate<ICaptionService, TelemetriaCaptionService>();

        return services;
    }

    /// <summary>
    /// Bootstrap multi-canal da Transcrição (US #146) — espelha <see cref="AddIngestaoMultiCanal"/>
    /// só no que a etapa precisa: OAuth Google keyed por ChannelId + cookies yt-dlp por canal.
    /// Não registra handlers de descoberta de vídeo. Lê canais ativos de canal_ingestao.
    /// </summary>
    public static IServiceCollection AddTranscricaoMultiCanal(this IServiceCollection services, IConfiguration config)
    {
        services.AddSupabaseCanalIngestao(config);

        using (var bootstrapProvider = services.BuildServiceProvider())
        {
            var canalRepository = bootstrapProvider.GetRequiredService<ICanalIngestaoRepository>();
            var canais = canalRepository.GetCanaisAtivosAsync().GetAwaiter().GetResult();

            var logger = bootstrapProvider.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Giom.VideoManager.Composition.AddTranscricaoMultiCanal");

            var falhas = new List<FalhaCredencialCanal>();
            var canaisValidos = services.AddYouTubeCredenciaisPorCanal(config, canais, logger, (c, e) => falhas.Add(new(c, e)));
            services.AddReporteFalhasCredencialCanais(falhas, canais.Count, canaisValidos.Count);

            // Cookies yt-dlp são independentes do OAuth — carregar pra TODOS os canais ativos,
            // mesmo se o RefreshToken estiver inválido (invalid_grant). Sem isso, YtDlpCaption
            // fica sem --cookies e a cadeia perde o provedor que a US #146 quer isolar por canal.
            var cookiesCanais = canais
                .Select(c => (c.ChannelId, c.SecretsKey))
                .ToList();
            services.AddYtDlpCookiesPorCanal(config, cookiesCanais, logger);

            services.AddSingleton<IReadOnlyList<CanalIngestaoConfig>>(canaisValidos);
        }

        return services;
    }

    /// <summary>
    /// Resolve a cadeia de config de execução de IA por canal (US #106) — prompt +
    /// Model/MaxTokens/UsarObservabilidade + a ORDEM do fallback, tudo numa linha por
    /// (canal, etapa, provedor). Duas origens (EtapaProvedorConfig:Provider):
    /// "Repository" (default, Supabase) ou "Arquivo" (Prompts/*.md + FallbackChain do
    /// appsettings, o caminho de hoje). Decorada com cache — "Memory" (default, por processo,
    /// suficiente pros workers de etapa que drenam fila e saem) ou "Redis" (compartilhado entre
    /// instâncias, ver EtapaProvedorConfig:Cache:Redis). Idempotente — chamada por
    /// AddSummaryService e AddCategorizacaoService, que podem coexistir no mesmo worker.
    /// </summary>
    public static IServiceCollection AddEtapaProvedorConfigService(this IServiceCollection services, IConfiguration config)
    {
        if (services.Any(d => d.ServiceType == typeof(IEtapaProvedorConfigService)))
            return services;

        var provider = config["EtapaProvedorConfig:Provider"] ?? "Repository";
        if (provider == "Arquivo")
        {
            services.AddPromptLoader();
            services.AddSingleton<IEtapaProvedorConfigService, ArquivoEtapaProvedorConfigService>();
        }
        else
        {
            services.AddSupabaseEtapaProvedorConfig(config);
            services.AddSingleton<IEtapaProvedorConfigService, RepositorioEtapaProvedorConfigService>();
        }

        var cacheProvider = config["EtapaProvedorConfig:Cache:Provider"] ?? "Memory";
        if (cacheProvider == "Redis")
            services.AddRedisCacheEtapaProvedorConfig(config);
        else
            services.Decorate<IEtapaProvedorConfigService, CacheEtapaProvedorConfigService>();

        return services;
    }

    /// <summary>
    /// Preço por 1M tokens de cada (provedor, modelo) — Task #130, substitui o antigo
    /// PricingCalculator estático. Fonte: tabela provedor_preco (Supabase), decorada com cache em
    /// memória (mesmo padrão de AddEtapaProvedorConfigService — sem Redis por enquanto, nenhum
    /// worker tem conexão configurada hoje). Idempotente — todo provedor de IA (Claude/OpenAI/
    /// Grok/Kimi/VoyageAI/Cohere) chama isso ao se registrar, podem coexistir no mesmo worker.
    /// </summary>
    public static IServiceCollection AddPricingService(this IServiceCollection services, IConfiguration config)
    {
        if (services.Any(d => d.ServiceType == typeof(IPricingService)))
            return services;

        services.AddSupabasePricing(config);
        services.Decorate<IPricingRepository, CachePricingRepository>();
        services.AddSingleton<IPricingService, PricingService>();

        return services;
    }

    /// <summary>
    /// Resumo — cadeia de fallback por canal (US #106; antes Feature #101, cadeia global fixa
    /// via Resumo:FallbackChain). Cada provedor (Claude/Grok/OpenAI) é registrado Keyed como
    /// IResumoProvedorService — não decide mais sozinho seu prompt/config, recebe por parâmetro
    /// (ver ClaudeResumoService). FallbackResumoService (único IResumoService público) resolve a
    /// cadeia certa do canal em runtime via IEtapaProvedorConfigService. SummarizeTech fica de
    /// fora desta migração — não usa prompt/config por canal (nenhum canal ativo o referencia
    /// hoje) e continua só disponível se registrado manualmente noutro lugar, se algum dia
    /// reativado.
    /// </summary>
    public static IServiceCollection AddSummaryService(this IServiceCollection services, IConfiguration config)
    {
        services.AddEtapaProvedorConfigService(config);
        services.AddPricingService(config);

        services.AddGrokSummary(config);
        services.AddKeyedSingleton<IResumoProvedorService>("Grok", (sp, _) => sp.GetRequiredService<GrokResumoService>());

        services.AddKimiSummary(config);
        services.AddKeyedSingleton<IResumoProvedorService>("Kimi", (sp, _) => sp.GetRequiredService<KimiResumoService>());

        services.AddOpenAiSummary(config);
        services.AddKeyedSingleton<IResumoProvedorService>("OpenAI", (sp, _) => sp.GetRequiredService<OpenAiResumoService>());

        services.AddClaudeSummary(config);
        services.AddKeyedSingleton<IResumoProvedorService>("Claude", (sp, _) => sp.GetRequiredService<ClaudeResumoService>());

        services.AddSingleton<IResumoService, FallbackResumoService>();
        services.Decorate<IResumoService, TelemetriaResumoService>();

        return services;
    }

    /// <summary>
    /// Categorização — cadeia de fallback por canal (US #106). Mesmo mecanismo de
    /// AddSummaryService.
    /// </summary>
    public static IServiceCollection AddCategorizacaoService(this IServiceCollection services, IConfiguration config)
    {
        services.AddEtapaProvedorConfigService(config);
        services.AddPricingService(config);

        services.AddGrokCategorizacao(config);
        services.AddKeyedSingleton<ICategorizacaoProvedorService>("Grok", (sp, _) => sp.GetRequiredService<GrokCategorizacaoService>());

        services.AddKimiCategorizacao(config);
        services.AddKeyedSingleton<ICategorizacaoProvedorService>("Kimi", (sp, _) => sp.GetRequiredService<KimiCategorizacaoService>());

        services.AddOpenAiCategorizacao(config);
        services.AddKeyedSingleton<ICategorizacaoProvedorService>("OpenAI", (sp, _) => sp.GetRequiredService<OpenAiCategorizacaoService>());

        services.AddClaudeCategorizacao(config);
        services.AddKeyedSingleton<ICategorizacaoProvedorService>("Claude", (sp, _) => sp.GetRequiredService<ClaudeCategorizacaoService>());

        services.AddSingleton<ICategorizacaoService, FallbackCategorizacaoService>();
        services.Decorate<ICategorizacaoService, TelemetriaCategorizacaoService>();

        return services;
    }

    /// <summary>Avaliador de conteúdo via IA (sempre Claude por enquanto) — usado pelo guardrail AvaliacaoIA.</summary>
    public static IServiceCollection AddAvaliadorIAService(this IServiceCollection services, IConfiguration config)
    {
        services.AddPricingService(config);
        return services.AddClaudeAvaliadorIA(config);
    }

    /// <summary>
    /// Embedding — cadeia por canal (US #129, mesmo mecanismo de AddSummaryService/US #106). Os
    /// 3 providers (OpenAI, VoyageAI, Cohere) são sempre registrados como Keyed Service — não
    /// decidem mais sozinhos sua ordem/ativação por canal, PipelineEmbeddingService/
    /// FallbackEmbeddingService resolvem isso via IEtapaProvedorConfigService (pelo ChannelId do
    /// vídeo/canal da busca). Embedding:FallbackChain no appsettings não é mais lido aqui. Cada
    /// provider embeda a pergunta/os chunks do seu próprio lado e marca a origem
    /// (VideoChunk.Origem) — a busca (IVideoChunkRepository.BuscarSimilaresAsync, Domain) filtra
    /// por origem antes de comparar vetores, então ter os 3 registrados não mistura espaços
    /// vetoriais incompatíveis mesmo que só 1 esteja na cadeia real de um canal.
    ///
    /// Indexação (IVideoIndexingService) e busca (IBuscaSemanticaService) são interfaces
    /// separadas (Feature #115, ISP) — PipelineEmbeddingService pra indexar (roda todos os
    /// providers da cadeia do canal) e FallbackEmbeddingService pra buscar (para no primeiro que
    /// funcionar). Cada consumidor (EmbeddingEtapaHandler indexa, ChatBotProcessor busca) injeta
    /// só a interface que usa.
    /// </summary>
    public static IServiceCollection AddEmbeddingService(this IServiceCollection services, IConfiguration config)
    {
        services.AddPricingService(config);
        services.AddEtapaProvedorConfigService(config);

        services.AddOpenAiEmbedding(config);
        services.AddKeyedSingleton<IEmbeddingService>("OpenAI", (sp, _) => sp.GetRequiredService<OpenAIEmbeddingService>());

        services.AddSupabaseFarolRateLimiter(config); // Bug #122
        services.AddVoyageEmbedding(config);
        services.AddKeyedSingleton<IEmbeddingService>("VoyageAI", (sp, _) => sp.GetRequiredService<VoyageEmbeddingService>());

        services.AddSupabaseFarolRateLimiter(config); // Bug #122 — idempotente
        services.AddCohereEmbedding(config);
        services.AddKeyedSingleton<IEmbeddingService>("Cohere", (sp, _) => sp.GetRequiredService<CohereEmbeddingService>());

        services.AddSingleton<IBuscaSemanticaService, FallbackEmbeddingService>();
        services.Decorate<IBuscaSemanticaService, TelemetriaBuscaSemanticaService>();

        services.AddSingleton<IVideoIndexingService, PipelineEmbeddingService>();
        services.Decorate<IVideoIndexingService, TelemetriaVideoIndexingService>();

        return services;
    }

    /// <summary>
    /// Agent de perguntas e respostas — cadeia de fallback (Feature #101), configurável via
    /// Agent:FallbackChain (default: Claude → Grok → OpenAI). Mesmo mecanismo de
    /// AddSummaryService.
    /// </summary>
    public static IServiceCollection AddAgentService(this IServiceCollection services, IConfiguration config)
    {
        services.AddPricingService(config);
        // Config First: a instrução de concisão do bot vem da tabela agente_config (cache de 1 min).
        services.AddSupabaseAgenteConfig(config);
        services.TryAddSingleton<IAgenteConfigService, AgenteConfigService>();
        var cadeiaNomes = config.GetSection("Agent:FallbackChain").Get<string[]>() ?? ["Claude", "Grok", "OpenAI"];

        foreach (var nome in cadeiaNomes)
        {
            switch (nome)
            {
                case "Grok":
                    services.AddGrokAgent(config);
                    services.AddKeyedSingleton<IAgentService>(nome, (sp, _) => sp.GetRequiredService<GrokAgentService>());
                    break;
                case "Kimi":
                    services.AddKimiAgent(config);
                    services.AddKeyedSingleton<IAgentService>(nome, (sp, _) => sp.GetRequiredService<KimiAgentService>());
                    break;
                case "OpenAI":
                    services.AddOpenAiAgent(config);
                    services.AddKeyedSingleton<IAgentService>(nome, (sp, _) => sp.GetRequiredService<OpenAiAgentService>());
                    break;
                default: // Claude
                    services.AddClaudeAgent(config);
                    services.AddKeyedSingleton<IAgentService>(nome, (sp, _) => sp.GetRequiredService<ClaudeAgentService>());
                    break;
            }
        }

        services.AddSingleton<IAgentService>(sp =>
        {
            var cadeia = cadeiaNomes.Select(nome => (nome, sp.GetRequiredKeyedService<IAgentService>(nome))).ToList();
            return new FallbackAgentService(cadeia, sp.GetRequiredService<ILogger<FallbackAgentService>>());
        });
        services.Decorate<IAgentService, TelemetriaAgentService>();

        return services;
    }

    /// <summary>
    /// Canais de chat ligados nesta execução — lista em <c>Chat:Canais</c> (default: Telegram).
    /// Mesmo mecanismo de AddAgentService/AddEmbeddingService: a composição lê a config e decide
    /// o que registrar, e quem chama (o worker) não precisa saber que Telegram ou WhatsApp
    /// existem.
    ///
    /// É LISTA, e não um "Chat:Provider" único como em Persistence, porque canais coexistem: o
    /// bot atende Telegram e WhatsApp ao mesmo tempo, cada um com o seu ChatBotProcessor.
    ///
    /// Estar na lista é a única chave que liga um canal — não existe mais uma flag "Habilitado"
    /// em paralelo dizendo a mesma coisa por outro caminho.
    /// </summary>
    public static IServiceCollection AddChatChannels(this IServiceCollection services, IConfiguration config)
    {
        var canais = config.GetSection("Chat:Canais").Get<string[]>() ?? [TelegramChatChannel.ChaveCanal];

        foreach (var nome in canais)
        {
            switch (nome.ToLowerInvariant())
            {
                case TelegramChatChannel.ChaveCanal:
                    services.AddTelegramChatChannel(config);
                    services.AddChatBotProcessorDoCanal(TelegramChatChannel.ChaveCanal);
                    break;

                case WhatsAppChatChannel.ChaveCanal:
                    // Além do canal, o WhatsApp precisa do caminho de entrada (fila + consumidor
                    // do webhook, Task #140) — saber disso é justamente papel da composição.
                    services.AddWhatsAppWebhook(config);
                    services.AddChatBotProcessorDoCanal(WhatsAppChatChannel.ChaveCanal);
                    break;

                default:
                    // Aqui NÃO se aplica o "default: provedor principal" que AddAgentService usa.
                    // Naquele caso qualquer elo da cadeia responde e o bot funciona; aqui, um
                    // typo em Chat:Canais deixaria o bot sem um canal inteiro — mudo pra quem
                    // escrevesse por ele — sem erro nenhum aparecer. Melhor não subir.
                    throw new InvalidOperationException(
                        $"Canal de chat desconhecido em Chat:Canais: '{nome}'. Conhecidos: " +
                        $"'{TelegramChatChannel.ChaveCanal}', '{WhatsAppChatChannel.ChaveCanal}'.");
            }
        }

        return services;
    }

    private const string ChaveFilaWhatsAppWebhook = "whatsapp-webhook";

    /// <summary>
    /// Canal WhatsApp completo: o adapter (Task #139) mais o caminho de ENTRADA das mensagens
    /// (Task #140).
    ///
    /// Ao contrário do Telegram, que busca mensagem sozinho (long polling), o WhatsApp é
    /// empurrado pela Meta num webhook HTTP. Como o ECS do bot não expõe porta pública — e abrir
    /// uma exigiria um load balancer a ~US$16/mês fixo, mais domínio e certificado —, quem recebe
    /// o POST é uma Lambda (infra/lambda-whatsapp-webhook): ela confere a assinatura e publica o
    /// corpo cru numa fila SQS. É essa fila que o consumidor registrado aqui drena.
    ///
    /// Efeito colateral bem-vindo: a fila é durável. Se o bot cair ou reiniciar no meio de um
    /// deploy, as perguntas ficam guardadas e são respondidas quando ele volta — coisa que uma
    /// entrega HTTP direta na porta do container não daria.
    /// </summary>
    /// Privado: quem liga o WhatsApp é a lista Chat:Canais, via AddChatChannels — não o worker.
    private static IServiceCollection AddWhatsAppWebhook(this IServiceCollection services, IConfiguration config)
    {
        services.AddWhatsAppChatChannel(config);
        services.AddFilaVideoEtapa<WhatsAppWebhookMensagem>(ChaveFilaWhatsAppWebhook, config, "FilaWhatsAppWebhook");

        services.AddSingleton(sp => new WhatsAppWebhookConsumidor(
            sp.GetRequiredKeyedService<IFilaConsumer<WhatsAppWebhookMensagem>>(ChaveFilaWhatsAppWebhook),
            sp.GetRequiredService<WhatsAppWebhookProcessor>(),
            sp.GetRequiredService<ILogger<WhatsAppWebhookConsumidor>>()));

        // O consumidor da fila é um laço próprio, ao lado do ChatBotProcessor do canal — o
        // entrypoint roda os dois sem saber que um deles é "coisa de WhatsApp".
        services.AddSingleton(sp => new CanalDeChatAtivo(
            "whatsapp:webhook",
            ct => sp.GetRequiredService<WhatsAppWebhookConsumidor>().ExecutarAsync(ct)));

        return services;
    }

    /// <summary>
    /// Bootstrap multi-canal pra ingestão unificada (US #105, Ingestao.Worker) — lê os canais
    /// ativos do Supabase (tabela canal_ingestao) SÍNCRONO durante ConfigureServices (mesmo
    /// padrão de resolver a credencial sincronamente no boot), registra um YouTubeService keyed por canal e um
    /// Func&lt;string, IVideoHandler&gt; resolvido preguiçosamente na primeira chamada — só
    /// depois que o container termina de montar, já que CanalIngestaoVideoHandler precisa de
    /// dependências registradas por OUTROS Add* nesta mesma composição (AddFilaVideoEtapa,
    /// AddTelemetriaService, AddTracingService), que podem rodar antes ou depois deste método.
    /// Composition não conhece SupabaseCanalIngestaoRepository nem GoogleCredentialOptions/
    /// YouTubeService diretamente — só a abstração ICanalIngestaoRepository (registrada por
    /// AddSupabaseCanalIngestao) e o método do adapter YouTube que encapsula toda a credencial
    /// (AddYouTubeCredenciaisPorCanal). Como a lista de canais precisa existir ANTES do
    /// container terminar de montar (host.Services ainda não existe neste ponto), resolve
    /// ICanalIngestaoRepository via um ServiceProvider temporário só pra essa leitura pontual —
    /// o registro real (usado pelo container final) continua vindo de AddSupabaseCanalIngestao.
    /// </summary>
    public static IServiceCollection AddIngestaoMultiCanal(this IServiceCollection services, IConfiguration config)
    {
        services.AddSupabaseCanalIngestao(config);

        IReadOnlyList<CanalIngestaoConfig> canaisValidos;
        using (var bootstrapProvider = services.BuildServiceProvider())
        {
            var canalRepository = bootstrapProvider.GetRequiredService<ICanalIngestaoRepository>();
            var canais = canalRepository.GetCanaisAtivosAsync().GetAwaiter().GetResult();

            // Logger vem do mesmo provider de bootstrap (já tem Serilog+Sentry configurados por
            // UseSerilog, registrado antes deste ConfigureServices no Host builder) — nada de
            // Console.WriteLine pra erro de credencial por canal. Resolvido e usado dentro do
            // using: o logger não precisa sobreviver ao descarte do provider temporário, só
            // durante esta chamada síncrona.
            var logger = bootstrapProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Giom.VideoManager.Composition.AddIngestaoMultiCanal");
            var falhas = new List<FalhaCredencialCanal>();
            canaisValidos = services.AddYouTubeCredenciaisPorCanal(config, canais, logger, (c, e) => falhas.Add(new(c, e)));
            services.AddReporteFalhasCredencialCanais(falhas, canais.Count, canaisValidos.Count);
        }

        services.AddSingleton<IReadOnlyList<CanalIngestaoConfig>>(canaisValidos);

        services.AddSingleton<Func<string, IVideoHandler>>(sp =>
        {
            var videoRepository = sp.GetRequiredService<IVideoRepository>();
            var transcricaoPublisher = sp.GetRequiredKeyedService<IFilaPublisher<VideoEtapaMensagem>>("transcricao");
            var sessionContext = sp.GetRequiredService<ISessionContext>();
            var tracingService = sp.GetRequiredService<ITracingService>();
            var canalPorId = canaisValidos.ToDictionary(c => c.ChannelId);
            var cache = new Dictionary<string, IVideoHandler>();

            return channelId =>
            {
                if (!cache.TryGetValue(channelId, out var handler))
                {
                    handler = new CanalIngestaoVideoHandler(canalPorId[channelId], videoRepository, transcricaoPublisher, sessionContext, tracingService);
                    cache[channelId] = handler;
                }
                return handler;
            };
        });

        return services;
    }

    /// <summary>
    /// ConversaHistorico:Provider — InMemory (padrão), Supabase, MongoDB. Decorado com
    /// FeedbackExternoConversaHistoricoService — reenvia o voto 👍/👎 pra um serviço de feedback
    /// externo (Helicone) sem que o canal (Telegram, etc.) precise saber que isso existe.
    /// </summary>
    public static IServiceCollection AddConversaHistorico(this IServiceCollection services, IConfiguration config)
    {
        var provider = config["ConversaHistorico:Provider"] ?? "InMemory";

        switch (provider)
        {
            case "Supabase":
                services.AddSupabaseConversaHistorico(config);
                break;
            case "MongoDB":
                services.AddMongoConversaHistorico(config);
                break;
            default:
                services.AddInMemoryConversaHistorico();
                break;
        }

        services.Decorate<IConversaHistoricoService, FeedbackExternoConversaHistoricoService>();
        return services;
    }

    /// <summary>
    /// Observabilidade externa de sessões/usuários/propriedades e feedback (hoje via Helicone,
    /// mas plugável — Claude*Service e o histórico de conversa só conhecem ISessionContext e
    /// IFeedbackExternoService, nunca o provedor concreto). Chamado explicitamente por cada
    /// worker que usa Claude (Agent/Resumo/Categorização) ou feedback (bot).
    /// </summary>
    public static IServiceCollection AddObservabilidadeExterna(this IServiceCollection services, IConfiguration config)
        => services.AddHelicone(config);

    /// <summary>
    /// Sinks de telemetria — hoje Supabase (interacao_log) + Sentry (breadcrumb) + alerta de gasto
    /// diário. Cada RegistrarAsync chega em TODOS via CompositeTelemetriaService; adicionar um novo
    /// sink no futuro é só registrar o tipo concreto aqui e incluir na lista.
    /// </summary>
    public static IServiceCollection AddTelemetriaService(this IServiceCollection services, IConfiguration config)
    {
        services.AddSupabaseTelemetria(config);
        services.AddSentryTelemetria();
        services.Configure<AlertaGastoOptions>(config.GetSection("AlertaGasto"));
        services.AddSingleton<ITelemetriaSink, AlertaGastoTelemetriaService>();

        // CompositeTelemetriaService recebe todo ITelemetriaSink registrado via
        // IEnumerable<ITelemetriaSink> — adicionar um sink novo no futuro é só implementar
        // ITelemetriaSink e registrar aqui, nada muda no Composite.
        services.AddSingleton<ITelemetriaService, CompositeTelemetriaService>();
        return services;
    }

    /// <summary>Rastreamento de etapas de uma interação (ex: busca RAG / agente / envio) — hoje via Sentry Performance.</summary>
    public static IServiceCollection AddTracingService(this IServiceCollection services, IConfiguration config)
        => services.AddSentryTracing(config);

    /// <summary>
    /// Fila de etapa (Feature #33 — Transcrição/Resumo/Categorização/Embedding, todas tratadas
    /// igual, sem caso especial) — registrada como Keyed Service (<paramref name="chave"/>, ex:
    /// "transcricao"/"resumo"), provider decidido dentro da própria seção raiz
    /// (<paramref name="secaoRaiz"/>:Provider, ex: "FilaTranscricao:Provider"/"FilaResumo:Provider").
    /// Genérico em <typeparamref name="TMensagem"/> desde a Feature #104 — a fila de eventos de
    /// conclusão (<c>EtapaConcluidaMensagem</c>) reaproveita este mesmo método com o tipo trocado.
    /// Cada worker de etapa chama isso só pra própria fila (uma etapa, um worker) — não mais um
    /// canal chamando várias vezes pras etapas que usa (ver Instituto.Worker/Ministerio.Worker,
    /// que agora só chamam pra "transcricao").
    /// </summary>
    public static IServiceCollection AddFilaVideoEtapa<TMensagem>(this IServiceCollection services, string chave, IConfiguration config, string secaoRaiz)
    {
        var provider = config[$"{secaoRaiz}:Provider"] ?? "Sqs";
        return provider == "RabbitMq"
            ? services.AddRabbitMqFilaEtapa<TMensagem>(chave, config, $"{secaoRaiz}:RabbitMq")
            : services.AddSqsFilaEtapa<TMensagem>(chave, config, $"{secaoRaiz}:Sqs");
    }

    /// <summary>
    /// IncertezaDetector:Provider — Heuristica (padrão). Deixado plugável (switch, não uma
    /// ligação direta) porque a forma de detectar incerteza numa resposta pode virar um
    /// classificador de IA ou outra lib no futuro, sem afetar quem consome o resultado.
    /// </summary>
    public static IServiceCollection AddIncertezaDetectorService(this IServiceCollection services, IConfiguration config)
    {
        var provider = config["IncertezaDetector:Provider"] ?? "Heuristica";

        return provider switch
        {
            _ => services.AddHeuristicaIncertezaDetector()
        };
    }

    /// <summary>
    /// GuardRails:Habilitados — lista de nomes do catálogo abaixo que roda em toda interação do
    /// bot (Segurança e Confiabilidade). Adicionar/remover um guardrail em produção é só editar
    /// essa lista no appsettings, sem mudar nem reimplantar código. Catálogo atual:
    /// PalavrasBloqueadas, PromptInjection, LimiteTamanhoMensagem, RateLimit (fase Entrada),
    /// RespostaVazia (fase Saida), AvaliacaoIA (fase configurável — usa IA em vez de regra fixa,
    /// ver GuardRailIAOptions:Instrucao) e AcessoRestrito (Board #22 — allowlist de chatIds;
    /// fica de fora de Habilitados por padrão, é opt-in do Mauro quando quiser restringir).
    /// </summary>
    public static IServiceCollection AddGuardRails(this IServiceCollection services, IConfiguration config)
    {
        services.AddGuardRailPipeline();

        var habilitados = config.GetSection("GuardRails:Habilitados").Get<string[]>() ?? [];

        foreach (var nome in habilitados)
        {
            switch (nome)
            {
                case "PalavrasBloqueadas":
                    services.Configure<PalavrasBloqueadasOptions>(config.GetSection("GuardRails:PalavrasBloqueadas"));
                    services.AddSingleton<IGuardRail, PalavrasBloqueadasGuardRail>();
                    break;
                case "PromptInjection":
                    services.AddSingleton<IGuardRail, PromptInjectionGuardRail>();
                    break;
                case "LimiteTamanhoMensagem":
                    services.Configure<LimiteTamanhoMensagemOptions>(config.GetSection("GuardRails:LimiteTamanhoMensagem"));
                    services.AddSingleton<IGuardRail, LimiteTamanhoMensagemGuardRail>();
                    break;
                case "RateLimit":
                    services.Configure<RateLimitOptions>(config.GetSection("GuardRails:RateLimit"));
                    services.AddSingleton<IGuardRail, RateLimitGuardRail>();
                    break;
                case "RespostaVazia":
                    services.AddSingleton<IGuardRail, RespostaVaziaGuardRail>();
                    break;
                case "AvaliacaoIA":
                    services.AddAvaliadorIAService(config);
                    services.Configure<GuardRailIAOptions>(config.GetSection("GuardRails:AvaliacaoIA"));
                    services.AddSingleton<IGuardRail, GuardRailIA>();
                    break;
                case "AcessoRestrito":
                    services.Configure<AllowlistControleAcessoOptions>(config.GetSection("GuardRails:AcessoRestrito"));
                    services.Configure<AcessoRestritoOptions>(config.GetSection("GuardRails:AcessoRestrito"));
                    services.AddSingleton<IControleAcessoService, AllowlistControleAcessoService>();
                    services.AddSingleton<IGuardRail, AcessoRestritoGuardRail>();
                    break;
            }
        }

        return services;
    }
}
