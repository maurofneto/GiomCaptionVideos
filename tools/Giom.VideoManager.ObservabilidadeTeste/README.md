# Giom.VideoManager.ObservabilidadeTeste

Console app pra testar a observabilidade da pipeline (Sentry Performance/Logs/Metrics, Helicone
Sessions, CorrelationId) **sem depender do YouTube** — usa `SupabaseVideoSource`/
`SupabaseCaptionService` no lugar dos providers reais, buscando vídeos e legendas que já existem
no Supabase (dev). Cada vídeo é devolvido com um Id novo (`teste-{guid}`), então
`InstitutoVideoHandler` nunca acha um "já processado" e sempre gera resumo/categorização/embedding
de novo — o conteúdo é real (título/legenda de um vídeo existente), só o Id é emprestado/novo.
Tudo o mais é real: `InstitutoVideoHandler`, Claude (resumo + categorização, via Helicone),
embedding (OpenAI), e persistência no Supabase dev.

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
Precisa que já existam vídeos com transcrição no Supabase dev (qualquer execução anterior do
Instituto — ou deste próprio tool — já deixa isso lá).

## Onde ver o resultado

Ambiente **`teste`** (Sentry e Helicone) — escolhido especificamente pra ficar fácil de filtrar,
sem se misturar com `dev`/`prod`.

- **Sentry**: Performance (transactions `pipeline.pagina`/`pipeline.video`) e Logs, ambiente `teste`.
- **Helicone**: Sessions — User = canal de teste, Session-Path
  `/pipeline-Development/Instituto Giom (Teste de Observabilidade)/pagina-primeira/{videoId}/resumo|categorizacao`.
- **Supabase dev**: tabelas `video`/`transcricao`/`resumo`/`categoria_teologica`/`video_chunk`,
  filtrando por `channel_id = 'supabase-instituto-teste'` ou `id LIKE 'teste-%'` — dá pra apagar
  essas linhas manualmente quando quiser limpar.
