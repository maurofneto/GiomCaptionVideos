namespace Giom.VideoManager.Domain.Interfaces;

/// <summary>
/// Detecta se um texto (tipicamente a resposta de um agente) admite incerteza — usado como
/// proxy de "buraco de conteúdo" na telemetria. Deixado genérico porque a forma de detectar
/// isso pode mudar (heurística de palavras-chave, classificador de IA, outra lib) sem afetar
/// quem consome o resultado — não é exclusivo de nenhum provider de IA especifico.
/// </summary>
public interface IIncertezaDetectorService
{
    bool ContemIncerteza(string texto);
}
