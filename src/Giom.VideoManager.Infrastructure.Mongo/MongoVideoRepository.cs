using Giom.VideoManager.Domain.Entities;
using Giom.VideoManager.Domain.Interfaces;
using Microsoft.Extensions.Options;

namespace Giom.VideoManager.Infrastructure.Mongo;

public class MongoOptions
{
    public string ConnectionString { get; init; } = string.Empty;
    public string DatabaseName { get; init; } = "giom";
}

/// <summary>
/// Repositório de vídeos usando MongoDB.
/// Requer pacote MongoDB.Driver — adicionar ao projeto quando necessário:
/// dotnet add package MongoDB.Driver
/// </summary>
public class MongoVideoRepository : IVideoRepository
{
    // TODO: implementar quando o pacote MongoDB.Driver for adicionado
    // private readonly IMongoCollection<Video> _collection;

    public MongoVideoRepository(IOptions<MongoOptions> options)
    {
        // var client = new MongoClient(options.Value.ConnectionString);
        // var db = client.GetDatabase(options.Value.DatabaseName);
        // _collection = db.GetCollection<Video>("videos");
        throw new NotImplementedException("MongoVideoRepository: adicione o pacote MongoDB.Driver e implemente este repositório.");
    }

    public Task<Video?> GetByIdAsync(string videoId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task UpsertAsync(Video video, CancellationToken ct = default)
        => throw new NotImplementedException();
}
