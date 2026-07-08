using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;

namespace Giom.VideoManager.Domain.ValueObjects;

/// <summary>
/// Mensagem publicada entre etapas do pipeline (Feature #33) — Resumo, Categorização e Embedding
/// reaproveitam o mesmo shape (o payload não muda entre etapas, só a fila e quem consome). A fila
/// de descoberta (Transcrição) continua usando <see cref="VideoParaProcessar"/>, sem mudança.
/// Sessao/TraceParent seguem o mesmo motivo de VideoParaProcessar — sem isso a árvore de spans e
/// as propriedades do Helicone se perderiam entre uma etapa e a próxima.
/// </summary>
public record VideoEtapaMensagem(Video Video, SessaoObservabilidade? Sessao, string? TraceParent);
