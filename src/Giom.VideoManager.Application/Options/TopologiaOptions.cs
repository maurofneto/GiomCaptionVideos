namespace Giom.VideoManager.Application.Options;

/// <summary>
/// Configuração da topologia do pipeline orquestrado (Feature #104) — só o PipelineHandler
/// (orquestrador) conhece isso; nenhum worker de etapa sabe o que vem depois de si mesmo.
/// <see cref="Canais"/> mapeia o ChannelId (YouTube) pro nome canônico do canal usado como
/// chave de <see cref="Topologia"/> — usa o Id, não o título exibido, porque título pode ter
/// acento/mudar; Id é estável.
/// <see cref="Topologia"/> é canal → etapa que acabou de terminar → lista de próximas etapas a
/// publicar (lista vazia = terminal, marca ProcessedAt). Fan-out (uma etapa acionando 2+ próximas
/// em paralelo, ex: Transcrição → Resumo + Embedding) é só a lista ter mais de 1 item.
/// </summary>
public class TopologiaOptions
{
    public Dictionary<string, string> Canais { get; init; } = new();
    public Dictionary<string, Dictionary<string, string[]>> Topologia { get; init; } = new();
}
