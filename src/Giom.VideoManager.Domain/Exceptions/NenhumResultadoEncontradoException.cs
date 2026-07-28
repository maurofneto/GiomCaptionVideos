namespace Giom.VideoManager.Domain.Exceptions;

/// <summary>
/// Um provedor (legenda, resumo, categorização...) rodou sem erro técnico, mas legitimamente
/// não produziu nada aproveitável — sem legenda encontrada, resposta vazia/truncada do modelo,
/// JSON malformado. Antes do Bug #134, esse caso virava um retorno "vazio" (null/lista vazia)
/// que a cadeia de fallback (<see cref="Giom.VideoManager.Infrastructure.Resiliencia.FallbackHandler{TResult}"/>)
/// só reagia a exceção pra tentar o próximo elo — então "não achei nada" era tratado como
/// sucesso, silenciosamente, e nunca chegava no próximo provedor da cadeia. Lançar esta exceção
/// em vez de retornar vazio faz esse caso entrar no MESMO mecanismo de fallback/retry/DLQ já
/// usado por qualquer outra falha — sem precisar de um segundo canal de sinalização.
/// </summary>
public class NenhumResultadoEncontradoException(string origem, string motivo, Exception? innerException = null)
    : Exception($"{origem}: {motivo}", innerException)
{
    public string Origem { get; } = origem;
}
