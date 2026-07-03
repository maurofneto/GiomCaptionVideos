using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Giom.VideoManager.Infrastructure.Mongo;

/// <summary>
/// Histórico de conversa persistido no MongoDB — sobrevive a reinícios do worker.
/// LimparAsync não apaga nada — incrementa a "geração" do chat (coleção chat_sessao,
/// via $inc atômico). O histórico antigo continua na coleção pra auditoria/depuração,
/// só deixa de ser visível pro agente porque a busca só enxerga a geração atual.
/// </summary>
public class MongoConversaHistoricoService : IConversaHistoricoService
{
    private const int MaxTurnos = 6;
    private readonly IMongoCollection<TurnoConversaDocumento> _collection;
    private readonly IMongoCollection<ChatSessaoDocumento> _sessoes;

    public MongoConversaHistoricoService(IOptions<MongoOptions> options)
    {
        var client = new MongoClient(options.Value.ConnectionString);
        var database = client.GetDatabase(options.Value.DatabaseName);
        _collection = database.GetCollection<TurnoConversaDocumento>("conversa_historico");
        _sessoes = database.GetCollection<ChatSessaoDocumento>("chat_sessao");
    }

    public async Task<IReadOnlyList<TurnoConversa>> ObterAsync(string chatId, CancellationToken ct = default)
    {
        var geracao = await ObterGeracaoAtualAsync(chatId, ct);

        var documentos = await _collection
            .Find(d => d.ChatId == chatId && d.Geracao == geracao)
            .SortByDescending(d => d.CriadoEm)
            .Limit(MaxTurnos)
            .ToListAsync(ct);

        documentos.Reverse();
        return documentos.Select(d => new TurnoConversa(d.Pergunta, d.Resposta)).ToList();
    }

    public async Task AdicionarAsync(string chatId, TurnoConversa turno, CancellationToken ct = default)
    {
        var geracao = await ObterGeracaoAtualAsync(chatId, ct);
        var documento = new TurnoConversaDocumento
        {
            ChatId = chatId,
            Pergunta = turno.Pergunta,
            Resposta = turno.Resposta,
            CriadoEm = DateTime.UtcNow,
            Geracao = geracao
        };
        await _collection.InsertOneAsync(documento, cancellationToken: ct);
    }

    public async Task LimparAsync(string chatId, CancellationToken ct = default)
    {
        // Não usa $inc direto: se ainda não existir documento de sessão, a geração "atual"
        // é 1 por padrão (ver ObterGeracaoAtualAsync) — $inc numa sessão inexistente partiria
        // de 0, gerando geracao=1 (igual ao padrão, ou seja, o /limpar não teria efeito nenhum).
        var atual = await ObterGeracaoAtualAsync(chatId, ct);
        var update = Builders<ChatSessaoDocumento>.Update.Set(d => d.Geracao, atual + 1);
        await _sessoes.UpdateOneAsync(
            d => d.ChatId == chatId,
            update,
            new UpdateOptions { IsUpsert = true },
            ct);
    }

    private async Task<int> ObterGeracaoAtualAsync(string chatId, CancellationToken ct)
    {
        var sessao = await _sessoes.Find(d => d.ChatId == chatId).FirstOrDefaultAsync(ct);
        return sessao?.Geracao ?? 1;
    }

    private class TurnoConversaDocumento
    {
        public ObjectId Id { get; set; }
        public string ChatId { get; set; } = string.Empty;
        public string Pergunta { get; set; } = string.Empty;
        public string Resposta { get; set; } = string.Empty;
        public DateTime CriadoEm { get; set; }
        public int Geracao { get; set; } = 1;
    }

    private class ChatSessaoDocumento
    {
        public ObjectId Id { get; set; }
        public string ChatId { get; set; } = string.Empty;
        public int Geracao { get; set; } = 1;
    }
}
