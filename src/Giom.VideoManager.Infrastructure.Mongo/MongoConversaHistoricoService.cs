using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Giom.VideoManager.Infrastructure.Mongo;

/// <summary>
/// Histórico de conversa persistido no MongoDB — sobrevive a reinícios do worker.
/// </summary>
public class MongoConversaHistoricoService : IConversaHistoricoService
{
    private const int MaxTurnos = 6;
    private readonly IMongoCollection<TurnoConversaDocumento> _collection;

    public MongoConversaHistoricoService(IOptions<MongoOptions> options)
    {
        var client = new MongoClient(options.Value.ConnectionString);
        var database = client.GetDatabase(options.Value.DatabaseName);
        _collection = database.GetCollection<TurnoConversaDocumento>("conversa_historico");
    }

    public async Task<IReadOnlyList<TurnoConversa>> ObterAsync(string chatId, CancellationToken ct = default)
    {
        var documentos = await _collection
            .Find(d => d.ChatId == chatId)
            .SortByDescending(d => d.CriadoEm)
            .Limit(MaxTurnos)
            .ToListAsync(ct);

        documentos.Reverse();
        return documentos.Select(d => new TurnoConversa(d.Pergunta, d.Resposta)).ToList();
    }

    public async Task AdicionarAsync(string chatId, TurnoConversa turno, CancellationToken ct = default)
    {
        var documento = new TurnoConversaDocumento
        {
            ChatId = chatId,
            Pergunta = turno.Pergunta,
            Resposta = turno.Resposta,
            CriadoEm = DateTime.UtcNow
        };
        await _collection.InsertOneAsync(documento, cancellationToken: ct);
    }

    public async Task LimparAsync(string chatId, CancellationToken ct = default)
    {
        await _collection.DeleteManyAsync(d => d.ChatId == chatId, ct);
    }

    private class TurnoConversaDocumento
    {
        public ObjectId Id { get; set; }
        public string ChatId { get; set; } = string.Empty;
        public string Pergunta { get; set; } = string.Empty;
        public string Resposta { get; set; } = string.Empty;
        public DateTime CriadoEm { get; set; }
    }
}
