using Microsoft.Extensions.Hosting;

namespace Infrastructure.Helpers;

/// <summary>
/// Carrega arquivos de prompt .md da pasta Prompts/ dentro do ContentRootPath do host.
/// Cada aplicação (worker) define seus próprios prompts em sua pasta Prompts/.
/// </summary>
public class PromptLoader
{
    private readonly string _promptsDir;

    public PromptLoader(IHostEnvironment env)
    {
        _promptsDir = Path.Combine(env.ContentRootPath, "Prompts");
    }

    public string Load(string fileName)
    {
        var path = Path.Combine(_promptsDir, fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Prompt não encontrado: {path}");
        return File.ReadAllText(path);
    }
}
