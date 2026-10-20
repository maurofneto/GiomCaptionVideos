# Config First (premissa para pessoas e IAs)

**Regra:** todo comportamento que alguém possa querer mudar sem deploy vive em **configuração**,
**preferencialmente no banco de dados** (Supabase). Código e `appsettings.json` carregam só o que
é infraestrutura ou segredo.

## Onde cada coisa mora

| Tipo | Onde | Exemplos |
|---|---|---|
| Comportamento de negócio/pipeline | **Banco** (tabela por assunto, RLS só `service_role`) | topologia (`etapa_topologia`), cadeia de provedores/prompts (`etapa_provedor_config`), canais (`canal_ingestao`), preços (`provedor_preco`), instrução de concisão do bot (`agente_config`) |
| Infraestrutura (como conectar) | `appsettings.json` | URL do Supabase, nomes de fila, região, Serilog |
| Segredos | SSM Parameter Store (nunca no repo) | chaves de API, cookies, SecretKey |

## Como aplicar (checklist ao criar/alterar algo configurável)

1. Perguntou-se "alguém vai querer mudar isso sem deploy?" Se sim, **vai para o banco**.
2. Tabela nova = migration em `supabase/migrations/` (+ `down/`), **seed** com o valor atual,
   RLS ligada com política `service_role_only_*`.
3. Leitura via repositório no Domain (`IXxxRepository`) com implementação em `Infrastructure.Supabase`
   e **cache curto** (≈1 min) — mudar a linha pega sem deploy nem reinício.
4. **Sem duplicar no `appsettings`**: nada de valor "padrão" escondido que contradiga o banco.
   O default no código só existe para degradar com segurança (ex.: bot sem instrução de concisão).
5. Falha de leitura: quem **decide fluxo** (orquestrador) falha alto e a mensagem é reentregue;
   quem só **ajusta resposta** (bot) degrada para o último valor conhecido.
6. Mudar em produção = `INSERT/UPDATE` no banco, documentado na US/PR. Nunca "só no código".

## Exemplos reais

- **Topologia** (`etapa_topologia`): `proximas` vazio = etapa terminal. Dar embedding ao Ministério
  é `INSERT ... ('UCyHMyZH5oon8jW7y_yMRstQ','transcricao', ARRAY['resumo','embedding'])`.
- **Concisão do bot** (`agente_config`): linha `'*'` vale para todos; linha do provedor
  (`Claude`, `Grok`, `Kimi`, `OpenAI`) vence. Vazio = `agent.md` inalterado.

- **Privacidade do vídeo** (`canal_ingestao.privacy_status_filtro`): `public` no Ministério (só vídeo público
  é indexado); `NULL` = sem restrição extra. Trocar é um `UPDATE`.

- **Preço por hora de áudio** (`provedor_preco.por_hora`): US$ 0,10/h do GrokVoice (xAI), ao lado do preço por milhão de tokens.
- **Relevância e teto por canal** (`canal_ingestao`: `relevancia_valores`, `privacy_status_filtro`, `max_videos_per_run`).

Operações de ingestão e recuperação: `docs/runbook-operacao-ingestao.md`.

Ver também: `docs/runbook-ambientes-banco.md` (dev x prod) e `docs/ARCHITECTURE.md`.
