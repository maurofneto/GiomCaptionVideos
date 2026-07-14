# Giom.VideoManager.SmokeTests

Smoke test: console app pra validar a pipeline inteira ponta a ponta (Transcrição → Resumo → Categorização →
Embedding, via a mesma orquestração central da Feature #104) **sem depender do YouTube** —
`FakeVideoSource`/`FakeCaptionService` geram vídeo/legenda 100% sintéticos, com
`Video.Id = "teste-{guid}"` (nunca colide com dado real; filtrável depois por
`id LIKE 'teste-%'`). ChannelId é o **real** do Instituto/Ministério Giom (US #106) — não mais um
canal fake — porque `etapa_provedor_config` tem FK pra `canal_ingestao(channel_id)`, então só um
canal real resolve prompt/`UsarObservabilidade` por canal de verdade. Persistência (Video/Resumo/
Categoria/Chunk) vai pro Supabase **dev** de verdade.

Cada `Fake*Service` injeta falha determinística por vídeo (ver `InjecaoDeFalha`) pra exercitar
retry/DLQ (Feature #34/US #89).

## Perfis

- **Padrão** (`appsettings.json`, `dotnet run` sem `DOTNET_ENVIRONMENT`) — Resumo/Categorização
  ficam nos `Fake*Service` (sem custo de API), só exercita Transcrição/Embedding + orquestração +
  observabilidade (Sentry/Helicone).
- **`teste-fallback`** — liga a cadeia REAL de Resumo com o `Model` do Claude propositalmente
  inválido, pra provar a queda pro próximo elo (Grok → OpenAI) de verdade.
- **`teste-etapa-provedor-config`** (US #106) — liga a cadeia REAL de Resumo **e** Categorização,
  sem quebra proposital, com a config vindo de `etapa_provedor_config` (Supabase dev) por
  ChannelId real — prova que o prompt por canal resolve (Instituto: "DEFINIÇÕES IMPORTANTES"/
  "CONCLUSÃO"; Ministério: "ILUSTRAÇÕES"/"APLICAÇÃO") e que a rota de observabilidade
  (`HeliconeRoteamentoHandler`) funciona sem o Claude/Grok/OpenAiXxxService saberem disso.

## Como rodar

```bash
dotnet build tests/Giom.VideoManager.SmokeTests/Giom.VideoManager.SmokeTests.csproj
cd tests/Giom.VideoManager.SmokeTests/bin/Debug/net9.0
DOTNET_ENVIRONMENT=teste-etapa-provedor-config dotnet Giom.VideoManager.SmokeTests.dll
```

> Rode o `.dll` a partir da pasta de build (não `dotnet run` direto na pasta do projeto) — o
> `PromptLoader` (usado só pelo modo Fake/Arquivo) resolve `Prompts/` relativo ao
> `ContentRootPath`, que só coincide com a pasta de build quando roda assim.

Usa as secrets de dev do pipeline (`/giom/dev/pipeline/` no SSM) — precisa de AWS autenticado
(`aws login` ou equivalente).

## Onde ver o resultado

Ambiente **`teste`** (Sentry e Helicone) — escolhido especificamente pra ficar fácil de filtrar,
sem se misturar com `dev`/`prod`.

- **Sentry**: Performance (transactions `pipeline.pagina`/`pipeline.video`) e Logs, ambiente `teste`.
- **Helicone**: Sessions — Session-Path
  `/pipeline/{ChannelTitle}/pagina-primeira/{videoId}/resumo|categorizacao` (só aparece quando
  `UsarObservabilidade=true` pra aquela linha de `etapa_provedor_config`).
- **Supabase dev**: tabelas `video`/`transcricao`/`resumo`/`categoria_teologica`/`video_chunk`,
  filtrando por `id LIKE 'teste-%'` — dá pra apagar essas linhas manualmente quando quiser limpar.

## Cuidado

`FilaResumo`/`FilaCategorizacao`/`FilaEmbedding`/`FilaTranscricao` apontam pras mesmas filas SQS
de dev que os workers reais (`giom-videos-resumo-dev` etc.) — se um worker de dev real também
estiver rodando/agendado, há corrida por mensagem. `FilaEtapaConcluida` já usa uma fila dedicada
(`giom-videos-etapa-concluida-teste-dev`), isolada.
