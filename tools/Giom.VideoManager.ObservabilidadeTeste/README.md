# Giom.VideoManager.ObservabilidadeTeste

Console app pra testar a observabilidade da pipeline (Sentry Performance/Logs/Metrics, Helicone
Sessions, CorrelationId) **sem depender do YouTube** — usa `FakeVideoSourceService` no lugar do
provider real, com 3 aulas teológicas inventadas (títulos/transcrições acentuados de propósito,
pra também exercitar a sanitização ASCII do Helicone). Tudo o mais é real: `InstitutoVideoHandler`,
Claude (resumo + categorização, via Helicone), embedding (OpenAI), e persistência no Supabase dev.

Cada execução gera vídeos com Id/UploadedAt novos, então rodar de novo sempre reprocessa os 3
vídeos (não é filtrado como "já processado").

## Como rodar

```bash
dotnet build tools/Giom.VideoManager.ObservabilidadeTeste/Giom.VideoManager.ObservabilidadeTeste.csproj
cd tools/Giom.VideoManager.ObservabilidadeTeste/bin/Debug/net9.0
DOTNET_ENVIRONMENT=Development dotnet Giom.VideoManager.ObservabilidadeTeste.dll
```

> Rode o `.dll` a partir da pasta de build (não `dotnet run` direto na pasta do projeto) — o
> `PromptLoader` resolve `Prompts/` relativo ao `ContentRootPath`, que só coincide com a pasta de
> build quando roda assim.

Usa as mesmas secrets de dev do Instituto (`/giom/dev/instituto/` no SSM) — precisa de AWS
autenticado (`aws sso login` ou equivalente) pra buscar Claude/Helicone/Sentry/Supabase/OpenAI keys.

## Onde ver o resultado

- **Sentry**: Performance (transactions `pipeline.pagina`/`pipeline.video`) e Logs, ambiente
  `instituto-obs-teste`.
- **Helicone**: Sessions — User = canal fake, Session-Path
  `/pipeline-Development/Instituto Giom (Teste de Observabilidade)/pagina-primeira/{videoId}/resumo|categorizacao`.
- **Supabase dev**: tabelas `video`/`transcricao`/`resumo`/`categoria_teologica`/`video_chunk`,
  filtrando por `channel_id = 'fake-instituto-observabilidade'` — dá pra apagar essas linhas
  manualmente quando quiser limpar.
