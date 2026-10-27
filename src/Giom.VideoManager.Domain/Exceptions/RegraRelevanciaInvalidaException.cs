namespace Giom.VideoManager.Domain.Exceptions;

/// <summary>
/// A regra de relevância de um canal (canal_ingestao.regra_relevancia) não pôde ser interpretada:
/// JSON malformado, campo/operador desconhecido ou valor incompatível. Quem carrega a regra
/// (repositório) trata isso caindo na regra anterior e registrando um erro, nunca derrubando a
/// ingestão do canal.
/// </summary>
public class RegraRelevanciaInvalidaException(string message) : Exception(message);
