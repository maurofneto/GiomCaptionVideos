using Giom.VideoManager.Application.Pipeline;
using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Giom.VideoManager.Domain.ValueObjects;
using Giom.VideoManager.Testes.Comuns;
using Xunit;

namespace Giom.VideoManager.Application.Tests.Pipeline;

/// <summary>
/// EtapaQueueConsumer é reaproveitado pelas etapas de Resumo/Categorização/Embedding (Feature #33)
/// — uma classe só, parametrizada pelo delegate da etapa e pelo nome do span. Estes testes cobrem
/// o mesmo contrato de VideoQueueConsumerTests: sessão/traceparent reaplicados, erro propaga sem
/// travar as próximas mensagens.
/// </summary>
public class EtapaQueueConsumerTests
{
    private static Video CriarVideo(string id) => new() { Id = id, Title = $"Vídeo {id}", ChannelTitle = "Canal de Teste" };

    private static VideoEtapaMensagem CriarMensagem(Video video, SessaoObservabilidade? sessao = null, string? traceParent = null) =>
        new(video, sessao, traceParent);

    private sealed class Ambiente
    {
        public FakeFilaConsumer<VideoEtapaMensagem> FilaConsumer { get; } = new();
        public FakeTracingService Tracing { get; } = new();
        public FakeSessionContext SessionContext { get; } = new();
        public FakeLogger<EtapaQueueConsumer> Logger { get; } = new();
        public List<Video> Processados { get; } = [];
        public Func<Video, CancellationToken, Task> Processar { get; set; } = (video, _) => Task.CompletedTask;

        public EtapaQueueConsumer CriarConsumer(string nomeSpan = "video.resumo") => new(
            FilaConsumer,
            (video, ct) =>
            {
                Processados.Add(video);
                return Processar(video, ct);
            },
            nomeSpan,
            Tracing,
            SessionContext,
            Logger);
    }

    [Fact]
    public async Task ConsumirAsync_ChamaODelegateParaCadaMensagem()
    {
        var ambiente = new Ambiente();
        var video1 = CriarVideo("v1");
        var video2 = CriarVideo("v2");
        ambiente.FilaConsumer.MensagensParaConsumir.Add(CriarMensagem(video1));
        ambiente.FilaConsumer.MensagensParaConsumir.Add(CriarMensagem(video2));

        await ambiente.CriarConsumer().ConsumirAsync();

        Assert.Equal([video1, video2], ambiente.Processados);
    }

    [Fact]
    public async Task ConsumirAsync_AbreSpanComNomeInformado()
    {
        var ambiente = new Ambiente();
        ambiente.FilaConsumer.MensagensParaConsumir.Add(CriarMensagem(CriarVideo("v1")));

        await ambiente.CriarConsumer("video.embedding").ConsumirAsync();

        Assert.Equal(["video.embedding"], ambiente.Tracing.Traces.Select(t => t.Nome));
    }

    [Fact]
    public async Task ConsumirAsync_PassaOTraceParentDaMensagemPraReatarAArvoreDeSpans()
    {
        var ambiente = new Ambiente();
        const string traceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        ambiente.FilaConsumer.MensagensParaConsumir.Add(CriarMensagem(CriarVideo("v1"), traceParent: traceParent));

        await ambiente.CriarConsumer().ConsumirAsync();

        Assert.Equal(traceParent, ambiente.Tracing.Traces[0].Trace.ContextoExternoRecebido);
    }

    [Fact]
    public async Task ConsumirAsync_QuandoDelegateLanca_MarcaOSpanEPropagaSemPararAsOutras()
    {
        var ambiente = new Ambiente();
        var video1 = CriarVideo("v1");
        var video2 = CriarVideo("v2");
        ambiente.FilaConsumer.MensagensParaConsumir.Add(CriarMensagem(video1));
        ambiente.FilaConsumer.MensagensParaConsumir.Add(CriarMensagem(video2));

        var excecao = new InvalidOperationException("falha simulada");
        ambiente.Processar = (video, _) => video.Id == "v1" ? throw excecao : Task.CompletedTask;

        await ambiente.CriarConsumer().ConsumirAsync();

        Assert.Single(ambiente.FilaConsumer.ErrosCapturados);
        Assert.Same(excecao, ambiente.FilaConsumer.ErrosCapturados[0]);
        Assert.True(ambiente.Tracing.Traces[0].Trace.Erro);
        Assert.Equal([video1, video2], ambiente.Processados);
    }

    [Fact]
    public async Task ConsumirAsync_SemSessaoNaMensagem_DefineSessaoComCanalVideoIdETitulo()
    {
        var ambiente = new Ambiente();
        var video = CriarVideo("v1");
        ambiente.FilaConsumer.MensagensParaConsumir.Add(CriarMensagem(video));

        await ambiente.CriarConsumer().ConsumirAsync();

        var sessao = Assert.Single(ambiente.SessionContext.Definidas);
        Assert.Equal("Canal de Teste", sessao.Propriedades!["Canal"]);
        Assert.Equal("v1", sessao.Propriedades!["VideoId"]);
    }

    [Fact]
    public async Task ConsumirAsync_ComSessaoNaMensagem_ReaplicaAMesmaSessaoDaPublicacao()
    {
        var ambiente = new Ambiente();
        var video = CriarVideo("v1");
        var sessaoDaPublicacao = new SessaoObservabilidade(
            SessionId: "corr-1",
            SessionName: "hora",
            SessionPath: "/pipeline-consumer/Canal/v1",
            UserId: "Canal",
            Propriedades: new Dictionary<string, string> { ["Canal"] = "Canal", ["VideoId"] = "v1" });
        ambiente.FilaConsumer.MensagensParaConsumir.Add(CriarMensagem(video, sessaoDaPublicacao));

        await ambiente.CriarConsumer().ConsumirAsync();

        var sessao = Assert.Single(ambiente.SessionContext.Definidas);
        Assert.Same(sessaoDaPublicacao, sessao);
    }
}
