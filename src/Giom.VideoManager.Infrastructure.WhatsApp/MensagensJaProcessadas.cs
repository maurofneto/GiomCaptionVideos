namespace Giom.VideoManager.Infrastructure.WhatsApp;

/// <summary>
/// Lembra os ids das mensagens já processadas pra descartar reentrega do Meta.
///
/// O Meta reentrega qualquer webhook que não receba 200 dentro do timeout dele — comportamento
/// documentado da plataforma, não caso raro. Sem esta trava, uma reentrega faria o bot chamar o
/// agente de novo pra MESMA pergunta: resposta duplicada pro aluno e cobrança duplicada de LLM.
///
/// Memória, não banco: a janela de reentrega do Meta é de minutos, então um cache em processo
/// resolve. Se o worker reiniciar bem no meio de uma reentrega, o pior caso é uma resposta
/// repetida — barato demais pra justificar estado persistido.
/// </summary>
internal sealed class MensagensJaProcessadas
{
    private const int Capacidade = 500;

    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private readonly Queue<string> _ordemDeChegada = new();
    private readonly Lock _trava = new();

    /// <summary>Devolve true se o id ainda não tinha aparecido (e passa a considerá-lo visto).</summary>
    public bool RegistrarSeNova(string mensagemId)
    {
        lock (_trava)
        {
            if (!_ids.Add(mensagemId)) return false;

            _ordemDeChegada.Enqueue(mensagemId);
            if (_ordemDeChegada.Count > Capacidade)
                _ids.Remove(_ordemDeChegada.Dequeue());

            return true;
        }
    }
}
