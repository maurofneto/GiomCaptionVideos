using System.Diagnostics;

namespace Giom.VideoManager.Infrastructure.Observabilidade;

/// <summary>
/// Fonte única de Activity (System.Diagnostics — nativo do .NET, não específico de nenhum
/// provedor) usada por toda a observabilidade de tracing do projeto. Uma Activity iniciada
/// enquanto outra está "atual" (Activity.Current) vira filha automaticamente — é assim que
/// SentryTracingService cria Spans dentro de uma Transaction sem precisar receber o pai
/// explicitamente. Precisa estar registrada via `.AddSource(Name)` no TracerProviderBuilder
/// (ver AddSentryTracing) pra ActivitySource.StartActivity parar de devolver null.
/// </summary>
public static class GiomActivitySource
{
    public const string Name = "Giom.VideoManager";

    public static readonly ActivitySource Instance = new(Name);
}
