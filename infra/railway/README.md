# PostgREST do Railway (dev) — token próprio e role `giom_pipeline`

Em **dev** os workers e os smoke tests falam com um PostgREST no Railway
(`Supabase:Url` = Railway, `Supabase:RestPrefix` = `""`, `Supabase:SecretKey` vazio) e hoje
dependem da **leitura anônima** (`PGRST_DB_ANON_ROLE`, que parece ser o próprio `postgres`).
Esta pasta troca isso por um JWT próprio com a role `giom_pipeline`, de grants mínimos, para
que a anônima possa ser fechada. **Prod não muda** (Supabase cloud, `RestPrefix = /rest/v1`,
`SecretKey` — continua exatamente como antes).

| Arquivo | Para quê |
|---|---|
| [`giom_pipeline_role.sql`](giom_pipeline_role.sql) | Cria a role e todos os grants/policies. Idempotente, uma transação, autoverificação das proibições. |
| este README | Regra do header, tabela de grants, chaves SSM por worker, passo a passo. |

**Por que o SQL não está em `supabase/migrations/`:** a pipeline raiz aplica aquela pasta
(`supabase db push`) automaticamente, a cada merge e sem revisão, no Supabase de dev **e de
prod**: a role e as policies seriam criadas lá também. Dev roda no Railway, não no Supabase.
Por isso é SQL manual versionado aqui, aplicado só no Postgres do Railway por quem tem o
banco de dev.

## 1. Token por config e regra do header

Opção nova: **`Supabase:PostgrestToken`** (`SupabaseOptions.PostgrestToken`). O valor real é
um JWT HS256 e vem **só do SSM** (ver §4) — nunca do `appsettings`: nos
`appsettings.Development.json` ele fica `""` com comentário, como o `SecretKey`.

A regra vive numa função pura, `SupabaseAuthHeaders.Resolve(restPrefix, secretKey,
postgrestToken)` (`src/Giom.VideoManager.Infrastructure.Supabase/SupabaseAuthHeaders.cs`), usada
por **todos** os clientes:

| `RestPrefix` | `SecretKey` | `PostgrestToken` | Header enviado |
|---|---|---|---|
| `""` | qualquer | preenchido | `Authorization: Bearer <token>` (sem `apikey`) |
| `""` | qualquer | vazio | nada (leitura anônima, como hoje) |
| `/rest/v1` | preenchido | qualquer | `Authorization: Bearer <SecretKey>` + `apikey: <SecretKey>` (prod, inalterado) |
| `/rest/v1` | vazio | qualquer | nada (como hoje) |

Clientes cobertos: o `HttpClient` nomeado `"supabase"` (`ServiceCollectionExtensions.cs`), que
atende `SupabaseVideoRepository`, `SupabaseVideoChunkRepository`,
`SupabaseVideoEtapaTerminalRepository`, `SupabaseProgressoRepository`,
`SupabaseConversaHistoricoService`, `SupabaseTelemetriaService`,
`SupabaseCanalIngestaoRepository`, `SupabaseEtapaProvedorConfigRepository`,
`SupabasePricingRepository`, `SupabaseFarolRateLimiter` (RPC) e `SupabaseEmbeddingService`; e
`SupabaseSecretsProvider`, o único que monta o próprio `HttpClient`. `git grep` não achou
nenhum outro chamador de PostgREST no repositório (`BackfillEmbeddings` usa o Supabase cloud
com `RestPrefix` padrão, ou seja, a linha 3 da tabela). O token nunca é logado: o record
sobrescreve `ToString`, nenhum log/exceção interpola credencial, e os testes
(`tests/Giom.VideoManager.Infrastructure.Tests/Supabase/`) provam isso nos caminhos de erro.

### `SupabaseSecretsProvider` e `app_secret` (decisão)

- **Ordem dos providers.** `AddSecretsProvider(config.Build())` constrói a config *parcial*
  (appsettings + variáveis de ambiente) e só então adiciona o provider de secrets. O
  `SupabaseSecretsProvider` roda nesse momento, **antes** do SSM existir na config. Logo o token
  dele só pode vir de `appsettings`/variável de ambiente (`Supabase__PostgrestToken`, p.ex.
  injetada pelo ECS a partir do SSM via `secrets` da task definition) — nunca do SSM
  carregado pelo `AwsParameterStoreSecretsProvider`. Todos os demais clientes leem a config
  final, com o SSM já aplicado.
- **`app_secret`: sem grant.** O provider lê `app_secret?select=key,value`, e `app_secret`
  guarda exatamente os segredos que a role não pode ver. A role **não** recebe nenhum
  privilégio nela (o SQL termina com uma checagem que aborta a transação se houver). Impacto:
  com `Secrets:Provider=Supabase` + token `giom_pipeline`, o provider leva 403. **Hoje isso é
  inócuo** — nenhum appsettings usa `Secrets:Provider=Supabase` (dev e prod usam
  `AwsParameterStore`). Alternativa, se um dia for preciso: usar o provider contra o Supabase
  cloud com `SecretKey` (service_role); ou, no Railway, uma role/JWT separada só para
  `app_secret`, fora de `giom_pipeline`.
- Mudança de comportamento do provider: antes mandava Bearer + `apikey` com o `SecretKey`
  sempre; agora segue a tabela acima (com `RestPrefix` vazio e sem token não manda nada).
  Para o único uso possível (Supabase cloud, `/rest/v1` + `SecretKey`) o resultado é idêntico.

## 2. Role `giom_pipeline` — o que cada worker usa

Uma role só para todos (é o que o JWT carrega). `PostgREST` → SQL: `GET`=SELECT, `POST`=INSERT,
`POST` com `Prefer: resolution=merge-duplicates`=INSERT + UPDATE (`ON CONFLICT DO UPDATE SET
<colunas do corpo>`), `PATCH`=UPDATE, `DELETE`=DELETE, `rpc/x`=EXECUTE. Caminhos de código
relativos a `src/Giom.VideoManager.Infrastructure.Supabase/` (abrev. **IS**) salvo indicação.

### Quais repositórios cada worker realmente chama

| Worker | Chamadas (evidência) |
|---|---|
| **Ingestao** | `GetByIdAsync` em `Application/Handlers/CanalIngestaoVideoHandler.cs:48`; progresso em `Application/Pipeline/ChannelProcessor.cs:97,98,192`; `canal_ingestao` no boot (`Composition/ServiceCollectionExtensions.cs:426,432`); telemetria (`Ingestao.Worker/Program.cs:45`) |
| **Transcricao** | `GetByIdAsync` + `UpsertAsync` em `Transcricao.Worker/TranscricaoEtapaHandler.cs:34,46`; `canal_ingestao` (`Composition/…:88,92`); `etapa_provedor_config` (`:68`→`:137`); telemetria (`Program.cs:42`) |
| **Resumo** | `Resumo.Worker/ResumoEtapaHandler.cs:28,48`; `etapa_provedor_config` + `provedor_preco` (`Composition/…:181,182`→`:137,:162`); telemetria |
| **Categorizacao** | `Categorizacao.Worker/CategorizacaoEtapaHandler.cs:28,47` (o `UpsertAsync` apaga e reinsere `categoria_teologica`, `IS/SupabaseVideoRepository.cs:85,95`); config + preço (`Composition/…:208,209`); telemetria |
| **Embedding** | `GetByIdAsync` em `Embedding.Worker/EmbeddingEtapaHandler.cs:25`; chunks: `DeletarPorVideoAsync`/`InserirAsync` em `Infrastructure.OpenAI/OpenAIEmbeddingService.cs:104-105`, `Infrastructure.Voyage/VoyageEmbeddingService.cs:94-95`, `Infrastructure.Cohere/CohereEmbeddingService.cs:92-93`; farol: `Voyage…:149`, `Cohere…:158`; `Composition/…:253-267`; telemetria |
| **Orquestracao** | `RegistrarConclusaoAsync` + `UpsertAsync` em `Application/Pipeline/PipelineHandler.cs:55,65`; telemetria (`Program.cs:34`) |
| **AgenteInstituto** | `conversa_historico`/`chat_sessao` (`ConversaHistorico:Provider=Supabase`, `AgenteInstituto.Worker/appsettings.json:65`; `Application/Pipeline/ChatBotProcessor.cs:123,125,201`, `BotComandosService.cs:33`, `Infrastructure.Telegram/TelegramChatChannel.cs:246`, `Infrastructure.WhatsApp/WhatsAppWebhookProcessor.cs:148`); farol/preço/config via `AddEmbeddingService` (`Composition/…:253-267`); telemetria (`Program.cs:43`). A busca RAG vai para a **Edge Function** `buscar-similar` (`IS/SupabaseVideoChunkRepository.cs:93`) — não é PostgREST, ver §6 |
| **SmokeTests** | `tests/Giom.VideoManager.SmokeTests/Program.cs:72,101,107,115,120,174` — a união de Resumo + Categorizacao + Embedding + Orquestracao + telemetria |

> Todo `UpsertAsync` grava o que o objeto `Video` carregar (`video` sempre; `transcricao`,
> `resumo` e `categoria_teologica` se presentes — `SupabaseVideoRepository.cs:75-78`), então a
> role não pode ser mais fina que isso por worker: é uma role única, e a tabela abaixo é a
> união.

### Tabela de grants

| Tabela / RPC | Verbos (SQL) | Colunas | Quem usa | Evidência |
|---|---|---|---|---|
| `video` | SELECT; INSERT+UPDATE (upsert `on_conflict=id`) | SELECT `*`; escrita: `id,title,description,channel_id,channel_title,playlist_id,length,privacy_status,uploaded_at,processed_at` | Ingestao, Transcricao, Resumo, Categorizacao, Embedding, Orquestracao, Smoke | `IS/SupabaseVideoRepository.cs:65,75,117-125` |
| `transcricao` | SELECT; INSERT+UPDATE (`on_conflict=video_id`) | SELECT `*`; escrita: `video_id,conteudo,formato,origem,gerado_em` | Transcricao (+ qualquer `UpsertAsync`) | `…VideoRepository.cs:65,76,151` |
| `resumo` | SELECT; INSERT+UPDATE (`on_conflict=video_id`) | SELECT `*`; escrita: `video_id,descricao,origem,gerado_em` | Resumo (+ qualquer `UpsertAsync`) | `…VideoRepository.cs:65,77,165` |
| `capitulo` | SELECT | `*` (embed `resumo(*,capitulo(*))`) | todos que chamam `GetByIdAsync` | `…VideoRepository.cs:65` — nenhum código grava capítulo |
| `categoria_teologica` | SELECT; DELETE; INSERT+UPDATE (POST merge-duplicates) | SELECT `video_id,area,subcategoria,peso`; escrita: `video_id,area,subcategoria,peso` | Categorizacao (+ qualquer `UpsertAsync` com categorias) | `…VideoRepository.cs:65,85,95-96,171` |
| `video_chunk` | SELECT; DELETE; INSERT+UPDATE (POST merge-duplicates) | SELECT `id,video_id,titulo,chunk_index,conteudo,embedding,origem` (as do SET do upsert; ver §3); escrita: `video_id,titulo,chunk_index,conteudo,embedding,origem` | Embedding (+ `GetByIdAsync` em todos pelo embed) | `IS/SupabaseVideoChunkRepository.cs:30-31,55-56,71`; `…VideoRepository.cs:65` |
| `video_etapa_terminal` | SELECT; INSERT+UPDATE (`on_conflict=video_id,etapa`) | SELECT `etapa` (+ chave); escrita: `video_id,etapa` | Orquestracao, Smoke | `IS/SupabaseVideoEtapaTerminalRepository.cs:34-35,45` |
| `processamento_progresso` | SELECT; INSERT+UPDATE (POST merge-duplicates) | SELECT `last_page_token`, `last_processed_date`; escrita: `channel_key,playlist_id,last_page_token,last_processed_date,last_execution_date` | Ingestao | `IS/SupabaseProgressoRepository.cs:36,48,70-71` |
| `canal_ingestao` | **só SELECT** (policy só `ativo`) | `*` | Ingestao, Transcricao | `IS/SupabaseCanalIngestaoRepository.cs:23` |
| `etapa_provedor_config` | SELECT | `*` | Transcricao, Resumo, Categorizacao, Embedding, AgenteInstituto, Smoke | `IS/SupabaseEtapaProvedorConfigRepository.cs:24` |
| `provedor_preco` | SELECT | `*` | Resumo, Categorizacao, Embedding, AgenteInstituto, Smoke | `IS/SupabasePricingRepository.cs:26` |
| `rpc/farol_consumir(text, jsonb)` | EXECUTE | — | Embedding, AgenteInstituto, Smoke | `IS/SupabaseFarolRateLimiter.cs:51` |
| `provedor_rate_limit` (via a função, que é `SECURITY INVOKER`) | SELECT; UPDATE (o `FOR UPDATE` exige UPDATE) | SELECT `*`; UPDATE `saldo, atualizado_em` | idem | `supabase/migrations/20260717150000_farol_rate_limiter.sql` (`farol_consumir`) |
| `interacao_log` | **só INSERT** (sem `RETURNING`) | `tipo,entidade_id,provedor,modelo,tempo_ms,tokens_entrada,tokens_saida,custo_estimado_usd,sucesso,detalhes` | todos | `IS/SupabaseTelemetriaService.cs:55` |
| `conversa_historico` | SELECT; INSERT (`return=representation` ⇒ SELECT `*`); UPDATE | SELECT `*`; INSERT `chat_id,pergunta,resposta,reasoning_content,geracao`; UPDATE `resposta,reasoning_content,feedback` | AgenteInstituto | `IS/SupabaseConversaHistoricoService.cs:47,63-64,82,90` |
| `chat_sessao` | SELECT; INSERT+UPDATE (POST merge-duplicates) | SELECT `*`; escrita `chat_id,geracao,atualizado_em` | AgenteInstituto | `IS/SupabaseConversaHistoricoService.cs:101-102,113` |
| sequences (`id` serial/identity) de `transcricao`, `resumo`, `categoria_teologica`, `video_chunk`, `conversa_historico`, `interacao_log` | USAGE, SELECT | — | quem insere nessas tabelas | os `INSERT` acima |
| **`app_secret`** | **nenhum** | — | só `SupabaseSecretsProvider`, que não roda em dev/prod | `IS/SupabaseSecretsProvider.cs:49` |
| views `v_*`, RPCs `match_*` | nenhum | — | o código C# não as chama via PostgREST (`match_*` rodam dentro das Edge Functions) | `git grep` |

**RLS.** As tabelas com RLS ligado têm policies `leitura_publica_*` (SELECT) e
`service_role_only_*`/`escrita_service_role_*` (`auth.role() = 'service_role'`, que a
`giom_pipeline` nunca satisfaz). O SQL adiciona, por tabela, policies **aditivas**
`TO giom_pipeline` (`giom_pipeline_select|insert|update|delete`, `DROP POLICY IF EXISTS` +
`CREATE POLICY`), só para os comandos da tabela acima — `INSERT … ON CONFLICT DO UPDATE` precisa
de SELECT + INSERT + UPDATE ao mesmo tempo, e `farol_consumir` (`FOR UPDATE`) de SELECT +
UPDATE. Elas não mudam o que `anon`/`service_role` enxergam; onde o RLS está desligado são
inertes.

## 3. Drift e pontos a conferir antes de aplicar

- **`conversa_historico`, `chat_sessao`, `interacao_log` e a coluna `categoria_teologica.peso`
  não existem em `supabase/migrations/`** (foram criadas fora do repositório; as migrations só
  as alteram ou referenciam). Os nomes de coluna vêm do código C#. Se uma coluna não existir, o
  `GRANT` falha e a transação volta atrás — confira com `\d <tabela>` antes. O mesmo vale para
  as PKs assumidas: `chat_sessao(chat_id)` e `categoria_teologica(id)`.
- O `UpsertAsync` em `video_chunk` e `categoria_teologica` usa `merge-duplicates` **sem**
  `on_conflict`, ou seja, conflito na PK `id` (que o corpo nunca envia). Na prática é só
  INSERT, mas o PostgREST ainda emite `ON CONFLICT (id) DO UPDATE SET …`, por isso o UPDATE
  por coluna e o `SELECT` em `video_chunk`.
- **Achado do Seal (PostgREST v12.2.12):** o upsert em `video_chunk` voltava 403 porque o
  `INSERT … ON CONFLICT("id") DO UPDATE SET conteudo = EXCLUDED.conteudo, embedding =
  EXCLUDED.embedding, titulo = EXCLUDED.titulo, …` exige SELECT nas colunas lidas via `EXCLUDED`
  e nas do alvo de conflito. **Regra: as colunas do SET do upsert (as do corpo enviado) e as do
  alvo de conflito precisam de SELECT.** Decisão do Mauro: manter o grant por coluna e dar
  SELECT também em `titulo`, `conteudo` e `embedding` (o SELECT de `video_chunk` agora é
  `id, video_id, titulo, chunk_index, conteudo, embedding, origem`; só `indexado_em` fica de
  fora). Efeito: a role pode ler o conteúdo e o embedding dos chunks; o código C# não mudou.
- Revisão dos demais upserts contra essa regra (nada a corrigir além de `video_chunk`):

  | Tabela | Alvo de conflito | Colunas do SET | SELECT concedido | Resultado |
  |---|---|---|---|---|
  | `video` | `id` | `id,title,description,channel_id,channel_title,playlist_id,length,privacy_status,uploaded_at,processed_at` | tabela inteira | ok |
  | `transcricao` | `video_id` | `video_id,conteudo,formato,origem,gerado_em` | tabela inteira | ok |
  | `resumo` | `video_id` | `video_id,descricao,origem,gerado_em` | tabela inteira | ok |
  | `categoria_teologica` | `id` (PK) | `video_id,area,subcategoria,peso` | tabela inteira | ok |
  | `video_etapa_terminal` | `video_id,etapa` | `video_id,etapa` | tabela inteira | ok |
  | `processamento_progresso` | `channel_key,playlist_id` | as 5 colunas do corpo | tabela inteira | ok |
  | `chat_sessao` | `chat_id` (PK assumida) | `chat_id,geracao,atualizado_em` | tabela inteira | ok |
  | `video_chunk` | `id` (PK) | `video_id,titulo,chunk_index,conteudo,embedding,origem` | por coluna, **faltavam `titulo`, `conteudo`, `embedding`** | corrigido |

  `conversa_historico` (POST simples + PATCH), `interacao_log` (POST simples) e
  `provedor_rate_limit` (UPDATE dentro da função) não são upsert; `conversa_historico` e
  `provedor_rate_limit` têm SELECT de tabela inteira.

### Para o Seal validar no banco descartável (os grants não mudam)

- [ ] **`interacao_log`**: o INSERT do PostgREST (sem `Prefer: return`, ou seja `return=minimal`)
  deve passar só com INSERT por coluna + policy de INSERT, sem grant nem policy de SELECT. O
  esperado é `RETURNING 1`/sem `RETURNING`; se vier `RETURNING` com colunas, o SELECT e a
  policy de SELECT passam a ser exigidos — aí o grant precisa ser revisto.
- [ ] **Upsert `merge-duplicates` sem `on_conflict`** em `video_chunk` e `categoria_teologica`
  (conflito na PK `id`): funciona com `SELECT (id, video_id, titulo, chunk_index, conteudo,
  embedding, origem)` por coluna em `video_chunk` + `INSERT`/`UPDATE` por coluna? (A versão
  anterior, sem `titulo/conteudo/embedding`, deu 403 no PostgREST v12.2.12 — corrigido; reconfirmar.)
- [ ] **`farol_consumir`**: o `SELECT … FOR UPDATE` dentro da função funciona só com
  `UPDATE (saldo, atualizado_em)` + `SELECT` em `provedor_rate_limit`, com RLS ligado?
- [ ] **Embeds** `video?select=*,transcricao(*),resumo(*,capitulo(*)),categoria_teologica(…),video_chunk(chunk_index,origem)`
  com RLS ligado em todas as tabelas envolvidas.
- [ ] **Idempotência**: rodar `giom_pipeline_role.sql` duas vezes seguidas sem erro e com o
  mesmo estado final (`\dp`, `\dRp`/`pg_policies`, `pg_auth_members`).
- [ ] **Sem `authenticator`**: rodar o script num banco sem essa role deve terminar com
  sucesso, emitindo só o `WARNING` do `GRANT … TO authenticator` pendente (é o estado de
  dev até o fechamento).

## 4. Chaves SSM por worker (dev)

O `AwsParameterStoreSecretsProvider` transforma `<Path><A>/<B>` em chave de config `A:B`. O
**mesmo JWT** vai em todos os paths (SecureString):

| Worker | `Secrets:Aws:Path(s)` (appsettings.Development) | Parâmetro SSM a criar | Vira a chave |
|---|---|---|---|
| Ingestao | `/giom/dev/canais/` | `/giom/dev/canais/Supabase/PostgrestToken` | `Supabase:PostgrestToken` |
| Transcricao | `Paths`: `/giom/dev/pipeline/`, `/giom/dev/canais/` | `/giom/dev/pipeline/Supabase/PostgrestToken` (o de `canais/` também é lido, e o último path vence — **mesmo valor**) | idem |
| Resumo | `/giom/dev/pipeline/` | `/giom/dev/pipeline/Supabase/PostgrestToken` | idem |
| Categorizacao | `/giom/dev/pipeline/` | idem | idem |
| Embedding | `/giom/dev/pipeline/` | idem | idem |
| Orquestracao | `/giom/dev/pipeline/` | idem | idem |
| AgenteInstituto | `/giom/dev/agenteinstituto/` | `/giom/dev/agenteinstituto/Supabase/PostgrestToken` | idem |
| SmokeTests | `/giom/dev/pipeline/` | `/giom/dev/pipeline/Supabase/PostgrestToken` | idem |

Total: **3 parâmetros** (`pipeline/`, `canais/`, `agenteinstituto/`). Prod não ganha nenhum
(continua com `SecretKey`). Esta mudança não altera IAM: a task role precisa já ler
`/giom/dev/*` (confirmar que a policy cobre os três paths — não verifiquei, é infra).

## 5. Passo a passo (Mauro + Lamp)

Nada abaixo foi executado por esta mudança. Ordem importa.

**Estado de partida (dev):** o PostgREST conecta como `postgres` e o `PGRST_JWT_SECRET` já foi
rotacionado pelo Lamp. Até o fechamento isso basta: `postgres` é superusuário e pode fazer
`SET ROLE` para qualquer role, então, quando a requisição traz um token com
`{"role":"giom_pipeline"}`, o PostgREST troca para `giom_pipeline` e os grants/policies deste
SQL valem de verdade (erro de grant aparece já, mesmo com a anônima aberta). `authenticator` e
a troca de `PGRST_DB_URI` só entram no **fechamento** (passo 5).

1. **Validar e aplicar o SQL.** O Seal valida (feito, com a correção do `video_chunk` do §3) e
   o SQL é aplicado no Railway dev **com a conexão atual**:
   `psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f infra/railway/giom_pipeline_role.sql`.
   O `WARNING` de `authenticator` inexistente é **esperado** nesta etapa. Opcional, se o
   PostgREST estava de pé antes: `NOTIFY pgrst, 'reload schema';`.
2. **O Lamp assina o JWT** HS256 com payload `{"role":"giom_pipeline"}`, usando o
   `PGRST_JWT_SECRET` já rotacionado, e grava o resultado em
   `/home/box/secrets/giom_pipeline_token_dev` (arquivo fora do repositório, em pasta de
   segredos nunca versionada nem colada em chat/log). Ninguém cola segredo nem token em chat,
   log ou argumento de comando.
   Recomenda-se **`exp`** (p.ex. 180 dias) para limitar o estrago de um vazamento — sem `exp`
   o token vale até o `PGRST_JWT_SECRET` ser trocado; anote a data de renovação. Exemplo para
   o Lamp (o segredo entra por `read -rs` e por variável de ambiente, não aparece na tela, no
   histórico nem na lista de processos):

   ```bash
   read -rs PGRST_JWT_SECRET && export PGRST_JWT_SECRET
   umask 077
   python3 -I - > /home/box/secrets/giom_pipeline_token_dev <<'PY'
   import base64, hashlib, hmac, json, os, time
   b64 = lambda b: base64.urlsafe_b64encode(b).rstrip(b"=")
   head = b64(json.dumps({"alg": "HS256", "typ": "JWT"}, separators=(",", ":")).encode())
   body = b64(json.dumps({"role": "giom_pipeline", "exp": int(time.time()) + 180 * 86400}, separators=(",", ":")).encode())
   sig = b64(hmac.new(os.environ["PGRST_JWT_SECRET"].encode(), head + b"." + body, hashlib.sha256).digest())
   print((head + b"." + body + b"." + sig).decode(), end="")
   PY
   unset PGRST_JWT_SECRET
   ```
3. **O Mauro grava no SSM** (SecureString, os 3 paths) a partir desse arquivo. `file://`
   evita o token em `argv`; no Git Bash, `MSYS_NO_PATHCONV=1` por causa do `/` inicial (ver
   `docs/ENGINEERING_STANDARDS.md`). Ajuste o caminho do `file://` se o arquivo estiver em
   outro lugar:

   ```bash
   for p in pipeline canais agenteinstituto; do
     MSYS_NO_PATHCONV=1 aws ssm put-parameter --name "/giom/dev/$p/Supabase/PostgrestToken" \
       --type SecureString --overwrite --value file:///home/box/secrets/giom_pipeline_token_dev
   done
   ```
4. **O Mauro força novo deploy dos workers dev** (eles só leem o SSM no boot). Services de
   longa duração (Transcricao, Resumo, Categorizacao, Embedding, Orquestracao,
   AgenteInstituto): `aws ecs update-service --cluster giom-cluster --service <svc-dev>
   --force-new-deployment` (ação de infra: confirmar antes). A Ingestao dev roda por
   schedule/`run-task` e pega o token na próxima execução; os smoke tests, na próxima rodada.
   **Conferir os logs** (CloudWatch dos workers dev e saída dos smoke tests): nenhum
   `Unauthorized`/`401`/`403`, `permission denied for table|sequence`, `JWSError`/`JWT`, e as
   escritas acontecendo (vídeo avança de etapa, `interacao_log` ganha linhas). O token nunca é
   logado; se aparecer em algum log, é bug.
5. **Fechamento**, só depois de **todos** os workers (inclusive AgenteInstituto) e os smoke
   tests OK com o token. O Lamp faz tudo junto: cria `authenticator` (`LOGIN NOINHERIT`), roda
   o SQL de novo (ou só `GRANT giom_pipeline TO authenticator`) para dar a membership, troca o
   `PGRST_DB_URI` para `authenticator` e remove `PGRST_DB_ANON_ROLE`. Depois, reconfere os
   logs. **Remover a anônima antes do passo 4 derruba os workers**: qualquer cliente sem
   token — worker que ainda não reiniciou, smoke test local, `BackfillEmbeddings` apontado ao
   Railway — passa a receber 401 (`PGRST302`). Trocar o `PGRST_DB_URI` sem a membership dá
   falha parecida (`permission denied to set role "giom_pipeline"`).

**Rollback:** antes do fechamento, para desligar o token num worker apague o parâmetro SSM e
reinicie (token vazio = leitura anônima, comportamento antigo). Se o fechamento der errado,
desfaça o bloco inteiro: recolocar `PGRST_DB_ANON_ROLE` e voltar o `PGRST_DB_URI` para
`postgres` restaura tudo na hora. Para revogar todos os tokens: trocar o `PGRST_JWT_SECRET`.
**Rotação:** o Lamp reassina (passo 2), o Mauro sobrescreve os 3 parâmetros (passo 3) e força
novo deploy (passo 4) antes do `exp`.

## 6. Dúvidas / limites conhecidos

- `AgenteInstituto` busca chunks pela Edge Function `functions/v1/buscar-similar` no
  `Supabase:Url`. O PostgREST do Railway não tem Edge Functions, então essa chamada já não
  funciona em dev (loga erro e devolve "nenhuma aula relevante"); esta mudança não a altera nem
  concede `match_*`. O mesmo vale para `SupabaseEmbeddingService` (hoje não registrado pela
  Composition). Se o RAG de dev precisa funcionar, é outra decisão.
- A execução padrão de funções no schema `public` é para `PUBLIC`; o script concede
  `EXECUTE` explícito em `farol_consumir` mas não revoga de `PUBLIC` (mudaria o contrato de
  outras roles). Com a anônima fechada isso não expõe nada ao mundo.
- O SQL assume o schema `public` exposto pelo PostgREST (`PGRST_DB_SCHEMAS`) e Postgres ≥ 14.
