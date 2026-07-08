namespace Giom.VideoManager.Infrastructure;

/// <summary>
/// Carrega arquivos de prompt .md da pasta Prompts/ ao lado do assembly em execução.
/// Cada aplicação (worker) define seus próprios prompts em sua pasta Prompts/.
/// Usa AppContext.BaseDirectory (não IHostEnvironment.ContentRootPath) de propósito —
/// ContentRootPath é o diretório de trabalho de onde o processo foi iniciado, que varia
/// (ex.: "dotnet run" usa a pasta do .csproj, sem Prompts/ copiado; rodar o .dll publicado usa a
/// pasta de build, com Prompts/ copiado) — BaseDirectory é sempre a pasta onde o assembly
/// realmente está, coerente com o CopyToOutputDirectory dos arquivos de prompt.
/// </summary>
public class PromptLoader
{
    private readonly string _promptsDir = Path.Combine(AppContext.BaseDirectory, "Prompts");

    public string Load(string fileName)
    {
        var path = Path.Combine(_promptsDir, fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Prompt não encontrado: {path}");
        return File.ReadAllText(path);
    }
}
