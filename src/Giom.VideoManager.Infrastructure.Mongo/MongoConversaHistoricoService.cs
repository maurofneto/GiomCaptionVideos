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
    private const string SequenciaId = "conversa_historico_feedback_id";
    private readonly IMongoCollection<TurnoConversaDocumento> _collection;
    private readonly IMongoCollection<ChatSessaoDocumento> _sessoes;
    private readonly IMongoCollection<SequenciaDocumento> _sequencias;

    public MongoConversaHistoricoService(IOptions<MongoOptions> options)
    {
        var client = new MongoClient(options.Value.ConnectionString);
        var database = client.GetDatabase(options.Value.DatabaseName);
        _collection = database.GetCollection<TurnoConversaDocumento>("conversa_historico");
        _sessoes = database.GetCollection<ChatSessaoDocumento>("chat_sessao");
        _sequencias = database.GetCollection<SequenciaDocumento>("sequencia");
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

    public async Task<long> AdicionarAsync(string chatId, TurnoConversa turno, CancellationToken ct = default)
    {
        var geracao = await ObterGeracaoAtualAsync(chatId, ct);

        // IConversaHistoricoService expõe o id como long (o tipo real da PK no Supabase, a
        // implementação de produção). O _id nativo do Mongo é um ObjectId, não numérico —
        // geramos um long próprio via $inc atômico numa coleção de sequência à parte, que
        // sobrevive a restart do worker (diferente de um contador em memória).
        var id = await ProximoIdAsync(ct);
        var documento = new TurnoConversaDocumento
        {
            FeedbackId = id,
            ChatId = chatId,
            Pergunta = turno.Pergunta,
            Resposta = turno.Resposta,
            CriadoEm = DateTime.UtcNow,
            Geracao = geracao
        };
        await _collection.InsertOneAsync(documento, cancellationToken: ct);

        return id;
    }

    public async Task RegistrarFeedbackAsync(string conversaId, bool util, CancellationToken ct = default)
    {
        if (!long.TryParse(conversaId, out var id)) return;

        var update = Builders<TurnoConversaDocumento>.Update.Set(d => d.Feedback, util ? 1 : -1);
        await _collection.UpdateOneAsync(d => d.FeedbackId == id, update, cancellationToken: ct);
    }

    private async Task<long> ProximoIdAsync(CancellationToken ct)
    {
        var update = Builders<SequenciaDocumento>.Update.Inc(d => d.Valor, 1);
        var opcoes = new FindOneAndUpdateOptions<SequenciaDocumento>
        {
            IsUpsert = true,
            ReturnDocument = ReturnDocument.After
        };
        var resultado = await _sequencias.FindOneAndUpdateAsync(d => d.Id == SequenciaId, update, opcoes, ct);
        return resultado.Valor;
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

        /// <summary>Id numérico exposto pela interface (ver AdicionarAsync) — não é o _id do Mongo.</summary>
        public long FeedbackId { get; set; }
        public string ChatId { get; set; } = string.Empty;
        public string Pergunta { get; set; } = string.Empty;
        public string Resposta { get; set; } = string.Empty;
        public DateTime CriadoEm { get; set; }
        public int Geracao { get; set; } = 1;
        public int? Feedback { get; set; }
    }

    private class ChatSessaoDocumento
    {
        public ObjectId Id { get; set; }
        public string ChatId { get; set; } = string.Empty;
        public int Geracao { get; set; } = 1;
    }

    private class SequenciaDocumento
    {
        public string Id { get; set; } = string.Empty;
        public long Valor { get; set; }
    }
}
