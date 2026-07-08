using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;

namespace Giom.VideoManager.Domain.ValueObjects;

/// <summary>
/// Mensagem publicada entre TODAS as etapas do pipeline orientado a eventos (Feature #33) —
/// Transcrição (fila de descoberta, publicada por FilaVideoHandler), Resumo, Categorização e
/// Embedding reaproveitam o mesmo shape (o payload não muda entre etapas, só a fila e quem
/// consome — Transcrição não é um caso especial, é só mais uma etapa). Sessao/TraceParent
/// carregam o contexto de observabilidade capturado no momento da publicação — sem isso a árvore
/// de spans no Sentry e as propriedades do Helicone se perderiam a cada hop.
/// </summary>
public record VideoEtapaMensagem(Video Video, SessaoObservabilidade? Sessao, string? TraceParent);
