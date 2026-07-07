using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;

namespace Giom.VideoManager.Domain.ValueObjects;

/// <summary>
/// Mensagem publicada na fila de ingestão — um vídeo novo detectado vira uma unidade de
/// trabalho independente (US #40), processada por quem consumir a fila. Carrega, além do vídeo,
/// o contexto de observabilidade capturado no momento da publicação:
/// - Sessao: a MESMA SessaoObservabilidade que estava ativa em ChannelProcessor (Environment/
///   Canal/Playlist/Pagina/VideoId/VideoTitle) — sem isso, o consumidor do outro lado reconstruía
///   uma sessão pobre, perdendo atributos no Sentry e no Helicone (Session-Path/Propriedades).
/// - TraceParent: o traceparent W3C (ver ITracingService.CapturarContextoAtual) do Span
///   "pipeline.video" aberto em ChannelProcessor — sem isso, o novo Span aberto no consumidor
///   (depois que o Span pai já fechou) vira uma Transaction raiz isolada em vez de filho de
///   pipeline.pagina, quebrando a árvore única que devia aparecer no Sentry.
/// </summary>
public record VideoParaProcessar(Video Video, SessaoObservabilidade? Sessao, string? TraceParent);
