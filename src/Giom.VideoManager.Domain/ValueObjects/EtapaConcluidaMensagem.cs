using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;

namespace Giom.VideoManager.Domain.ValueObjects;

/// <summary>
/// Mensagem publicada por um worker de etapa (Feature #104) quando termina o próprio trabalho —
/// o worker de etapa não sabe (nem precisa saber) o que vem depois; só avisa que terminou.
/// Consumida pelo PipelineHandler (orquestrador), que é o único lugar que conhece a topologia
/// por canal e decide pra onde publicar em seguida (ou marcar ProcessedAt, se for terminal).
/// Etapa usa a mesma string já usada como chave de Keyed Service ("transcricao", "resumo",
/// "categorizacao", "embedding") — não introduz vocabulário novo.
/// </summary>
public record EtapaConcluidaMensagem(Video Video, string Etapa, SessaoObservabilidade? Sessao, string? TraceParent);
