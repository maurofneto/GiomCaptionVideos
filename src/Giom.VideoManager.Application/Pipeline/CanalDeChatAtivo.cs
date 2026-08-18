namespace Giom.VideoManager.Application.Pipeline;

/// <summary>
/// Marca um canal de chat ligado nesta execução (US #24). O worker do bot resolve todos e sobe
/// um <see cref="ChatBotProcessor"/> pra cada um.
///
/// Existe pra que o Program.cs não precise saber QUAIS canais existem: quem decide isso é a
/// composição, lendo config. Acrescentar um terceiro canal um dia não muda o entrypoint.
/// </summary>
public record CanalDeChatAtivo(string Chave);
