using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Options;

namespace Giom.VideoManager.Infrastructure.Mongo;

/// <summary>
/// Repositório de progresso usando MongoDB.
/// Requer pacote MongoDB.Driver — adicionar ao projeto quando necessário.
/// </summary>
public class MongoProgressoRepository : IProgressoRepository
{
    public MongoProgressoRepository(IOptions<MongoOptions> options)
    {
        throw new NotImplementedException("MongoProgressoRepository: adicione o pacote MongoDB.Driver e implemente este repositório.");
    }

    public Task<string?> GetLastPageTokenAsync(string channelKey, string playlistId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<DateTime?> GetLastProcessedDateAsync(string channelKey, string playlistId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<int> GetPaginaOffsetAsync(string channelKey, string playlistId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task SaveProgressoAsync(string channelKey, string playlistId, string? pageToken, DateTime? maxProcessedDate, int paginaOffset, CancellationToken ct = default)
        => throw new NotImplementedException();
}
