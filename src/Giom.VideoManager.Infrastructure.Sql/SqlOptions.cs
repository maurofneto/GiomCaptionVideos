namespace Giom.VideoManager.Infrastructure.Sql;

public class SqlOptions
{
    // "set" (não "init") porque é populada via Configure<SqlOptions>(Action<T>) em
    // ServiceCollectionExtensions, não por section binding — o binder de configuração
    // usa reflection e "set" comum, mas Action<T> precisa do setter em tempo de execução.
    public string ConnectionString { get; set; } = string.Empty;
}
