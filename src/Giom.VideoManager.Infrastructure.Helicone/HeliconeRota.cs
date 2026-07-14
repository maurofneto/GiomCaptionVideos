namespace Giom.VideoManager.Infrastructure.Helicone;

/// <summary>
/// Como rotear as chamadas de UM provedor específico (Claude/OpenAI/Grok) pro Helicone —
/// Anthropic e OpenAI têm domínio dedicado (troca só o host, ex.: anthropic.helicone.ai);
/// provedores sem domínio dedicado (ex.: xAI/Grok) passam pelo Gateway genérico do Helicone
/// (gateway.helicone.ai), que exige informar o destino real via headers
/// Helicone-Target-Url/Helicone-Target-Provider. Ver HeliconeRoteamentoHandler.
/// </summary>
public sealed record HeliconeRota(string HostGateway, string? NomeProvedorAlvo = null)
{
    public bool ViaGatewayGenerico => NomeProvedorAlvo is not null;

    public static HeliconeRota DominioDedicado(string host) => new(host);

    public static HeliconeRota GatewayGenerico(string nomeProvedorAlvo) => new("gateway.helicone.ai", nomeProvedorAlvo);
}
