using Giom.VideoManager.Domain.Interfaces;

namespace Giom.VideoManager.Testes.Comuns;

/// <summary>
/// Fake de IFilaConsumer — MensagensParaConsumir simula o que estaria na fila; ConsumirAsync
/// chama o handler pra cada uma, na ordem. Mesma semântica dos consumers reais (SqsFilaConsumer/
/// RabbitMqFilaConsumer): se o handler lançar, a exceção é capturada (não derruba o consumo das
/// próximas mensagens) e fica disponível em ErrosCapturados pra asserção.
/// </summary>
public class FakeFilaConsumer<TMensagem> : IFilaConsumer<TMensagem>
{
    public List<TMensagem> MensagensParaConsumir { get; } = [];
    public List<Exception> ErrosCapturados { get; } = [];

    public async Task ConsumirAsync(Func<TMensagem, CancellationToken, Task> handler, CancellationToken ct = default)
    {
        foreach (var mensagem in MensagensParaConsumir)
        {
            try
            {
                await handler(mensagem, ct);
            }
            catch (Exception ex)
            {
                ErrosCapturados.Add(ex);
            }
        }
    }
}
