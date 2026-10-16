namespace Giom.VideoManager.Application.Pipeline;

/// <summary>
/// Um canal de chat ligado nesta execução, junto com o laço que o mantém funcionando (US #24).
///
/// Na maioria dos casos o laço é o <see cref="ChatBotProcessor"/> daquele canal escutando as
/// mensagens. O WhatsApp registra um segundo, o consumidor da fila que a Lambda do webhook
/// alimenta (Task #140) — o canal não se sustenta só com o processor, porque as mensagens não
/// chegam sozinhas até ele.
///
/// Existe pra que o Program.cs não saiba QUAIS canais existem nem o que cada um precisa pra
/// funcionar: essa decisão é da composição, que lê a config. Acrescentar um canal novo deixa de
/// mexer no entrypoint.
/// </summary>
/// <param name="Chave">Identificação do laço, usada em log e diagnóstico.</param>
/// <param name="Executar">Roda até o token ser cancelado.</param>
public record CanalDeChatAtivo(string Chave, Func<CancellationToken, Task> Executar);
