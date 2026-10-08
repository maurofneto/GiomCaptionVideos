-- =====================================================================================
-- Role giom_pipeline — acesso mínimo dos workers/smoke tests ao PostgREST do Railway (dev).
--
-- NÃO é migration: fica fora de supabase/migrations/ porque a pipeline raiz aplica aquela
-- pasta automaticamente, a cada merge e sem revisão, no Supabase de dev E no de prod —
-- criaria a role e as policies lá também. Dev roda no Railway, não no Supabase; este script
-- é aplicado à mão, só no Postgres do Railway. Ver infra/railway/README.md.
--
-- Como aplicar (como superusuário/dono no Railway dev; sem segredo nenhum neste arquivo):
--     psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f infra/railway/giom_pipeline_role.sql
--
-- Idempotente e declarativo: tudo roda numa transação, zera os privilégios da role no schema
-- public e reconcede só o que está abaixo — rodar de novo converge para o mesmo estado, e um
-- grant removido daqui some do banco na próxima execução.
--
-- Fonte de cada grant: tabela "worker → tabela/RPC → verbos → colunas → evidência" em
-- infra/railway/README.md. Princípios:
--   * SELECT em tabela inteira onde o código lê com select=* / sem select (ou RETURNING *);
--   * INSERT/UPDATE sempre por coluna, só as do corpo enviado pelo código. UPDATE existe
--     porque POST com "Prefer: resolution=merge-duplicates" vira INSERT ... ON CONFLICT DO
--     UPDATE SET <colunas do corpo> (inclui as colunas da chave de conflito);
--   * DELETE só em categoria_teologica e video_chunk (o código apaga e reinsere);
--   * NADA em app_secret; canal_ingestao é só leitura; views v_* e RPCs match_* não são
--     usadas pelo código C# via PostgREST (as match_* rodam dentro das Edge Functions).
--
-- Drift conhecido: conversa_historico, chat_sessao, interacao_log e categoria_teologica.peso
-- NÃO estão em supabase/migrations/ (foram criadas fora do repositório). Os nomes de coluna
-- abaixo vêm do código C# que lê/escreve essas tabelas. Se alguma coluna não existir, o
-- GRANT falha e a transação inteira volta atrás (nada é aplicado pela metade).
-- =====================================================================================

BEGIN;

-- ---------- 1. Role --------------------------------------------------------------------
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'giom_pipeline') THEN
        CREATE ROLE giom_pipeline NOLOGIN NOINHERIT;
    END IF;
END $$;

-- Reafirma os atributos mesmo se a role já existia com outros.
ALTER ROLE giom_pipeline NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;

-- No FECHAMENTO da anônima o PostgREST passa a conectar como authenticator (LOGIN NOINHERIT,
-- criada pelo Lamp) e troca para a role do claim "role" do JWT com SET ROLE — por isso
-- authenticator precisa ser MEMBER. Até lá a conexão é postgres (superusuário, que pode
-- SET ROLE para qualquer role) e authenticator não existe.
-- Guardado por existência: sem authenticator, só avisa (esperado antes do fechamento; rode
-- este script de novo, ou só o GRANT, depois que o Lamp criar a role).
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticator') THEN
        IF NOT pg_has_role('authenticator', 'giom_pipeline', 'MEMBER') THEN
            GRANT giom_pipeline TO authenticator;
        END IF;
    ELSE
        RAISE WARNING 'role authenticator não existe ainda — rode este script de novo depois que ela for criada (GRANT giom_pipeline TO authenticator ficou pendente)';
    END IF;
END $$;

-- ---------- 2. Baseline: zera e reconcede ------------------------------------------------
REVOKE ALL ON ALL TABLES    IN SCHEMA public FROM giom_pipeline;  -- inclui privilégios por coluna
REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM giom_pipeline;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA public FROM giom_pipeline;

GRANT USAGE ON SCHEMA public TO giom_pipeline;

-- ---------- 3. Tabelas da pipeline de vídeo (SupabaseVideoRepository) -------------------------
-- video: GET com embed (select=*,transcricao(*),resumo(*,capitulo(*)),categoria_teologica(...),
-- video_chunk(chunk_index,origem)) + upsert on_conflict=id.
GRANT SELECT ON public.video TO giom_pipeline;
GRANT INSERT (id, title, description, channel_id, channel_title, playlist_id, length, privacy_status, uploaded_at, processed_at),
      UPDATE (id, title, description, channel_id, channel_title, playlist_id, length, privacy_status, uploaded_at, processed_at)
    ON public.video TO giom_pipeline;

-- transcricao: embed (*) + upsert on_conflict=video_id.
GRANT SELECT ON public.transcricao TO giom_pipeline;
GRANT INSERT (video_id, conteudo, formato, origem, gerado_em),
      UPDATE (video_id, conteudo, formato, origem, gerado_em)
    ON public.transcricao TO giom_pipeline;

-- resumo: embed (*) + upsert on_conflict=video_id.
GRANT SELECT ON public.resumo TO giom_pipeline;
GRANT INSERT (video_id, descricao, origem, gerado_em),
      UPDATE (video_id, descricao, origem, gerado_em)
    ON public.resumo TO giom_pipeline;

-- capitulo: só o embed resumo(*,capitulo(*)). Nenhum código C# grava capitulo.
GRANT SELECT ON public.capitulo TO giom_pipeline;

-- categoria_teologica: embed (video_id,area,subcategoria,peso); DELETE por video_id e depois
-- POST merge-duplicates (conflito na PK).
GRANT SELECT ON public.categoria_teologica TO giom_pipeline;
GRANT INSERT (video_id, area, subcategoria, peso),
      UPDATE (video_id, area, subcategoria, peso)
    ON public.categoria_teologica TO giom_pipeline;
GRANT DELETE ON public.categoria_teologica TO giom_pipeline;

-- video_chunk (SupabaseVideoChunkRepository): SELECT por coluna, mas com TODAS as colunas do
-- corpo do upsert. O POST merge-duplicates vira INSERT ... ON CONFLICT("id") DO UPDATE SET
-- titulo = EXCLUDED.titulo, conteudo = EXCLUDED.conteudo, embedding = EXCLUDED.embedding, ...
-- e o Postgres exige SELECT nas colunas lidas via EXCLUDED e nas do alvo de conflito (id) —
-- sem isso o PostgREST v12.2.12 devolve 403 (achado do Seal). Regra: colunas do SET do upsert
-- precisam de SELECT. Efeito colateral aceito (decisão do Mauro): a role passa a poder ler
-- titulo/conteudo/embedding; o grant continua por coluna, fora dele fica o resto (ex.: indexado_em).
GRANT SELECT (id, video_id, titulo, chunk_index, conteudo, embedding, origem) ON public.video_chunk TO giom_pipeline;
GRANT INSERT (video_id, titulo, chunk_index, conteudo, embedding, origem),
      UPDATE (video_id, titulo, chunk_index, conteudo, embedding, origem)
    ON public.video_chunk TO giom_pipeline;
GRANT DELETE ON public.video_chunk TO giom_pipeline;

-- video_etapa_terminal (SupabaseVideoEtapaTerminalRepository): upsert on_conflict=video_id,etapa + GET etapa.
GRANT SELECT ON public.video_etapa_terminal TO giom_pipeline;
GRANT INSERT (video_id, etapa),
      UPDATE (video_id, etapa)
    ON public.video_etapa_terminal TO giom_pipeline;

-- processamento_progresso (SupabaseProgressoRepository): GET + POST merge-duplicates (PK).
GRANT SELECT ON public.processamento_progresso TO giom_pipeline;
GRANT INSERT (channel_key, playlist_id, last_page_token, last_processed_date, last_execution_date),
      UPDATE (channel_key, playlist_id, last_page_token, last_processed_date, last_execution_date)
    ON public.processamento_progresso TO giom_pipeline;

-- ---------- 4. Configuração operacional: SOMENTE leitura -------------------------------------
GRANT SELECT ON public.canal_ingestao        TO giom_pipeline;  -- NUNCA INSERT/UPDATE/DELETE (conferido no fim)
GRANT SELECT ON public.etapa_provedor_config TO giom_pipeline;
GRANT SELECT ON public.provedor_preco        TO giom_pipeline;

-- ---------- 5. Farol de rate limit (SupabaseFarolRateLimiter → rpc/farol_consumir) ---------
-- farol_consumir é SECURITY INVOKER: lê, trava (SELECT ... FOR UPDATE exige UPDATE) e atualiza
-- provedor_rate_limit com os privilégios de quem chama.
GRANT EXECUTE ON FUNCTION public.farol_consumir(text, jsonb) TO giom_pipeline;
GRANT SELECT ON public.provedor_rate_limit TO giom_pipeline;
GRANT UPDATE (saldo, atualizado_em) ON public.provedor_rate_limit TO giom_pipeline;

-- ---------- 6. Telemetria e bot ---------------------------------------------------------
-- interacao_log: POST sem Prefer return=representation → só INSERT (sem RETURNING, sem SELECT).
GRANT INSERT (tipo, entidade_id, provedor, modelo, tempo_ms, tokens_entrada, tokens_saida, custo_estimado_usd, sucesso, detalhes)
    ON public.interacao_log TO giom_pipeline;

-- conversa_historico: GET; POST com return=representation (RETURNING * → SELECT da tabela);
-- PATCH resposta/reasoning_content (AtualizarAsync) e feedback (RegistrarFeedbackAsync).
GRANT SELECT ON public.conversa_historico TO giom_pipeline;
GRANT INSERT (chat_id, pergunta, resposta, reasoning_content, geracao) ON public.conversa_historico TO giom_pipeline;
GRANT UPDATE (resposta, reasoning_content, feedback) ON public.conversa_historico TO giom_pipeline;

-- chat_sessao: GET geracao + POST merge-duplicates (PK).
GRANT SELECT ON public.chat_sessao TO giom_pipeline;
GRANT INSERT (chat_id, geracao, atualizado_em),
      UPDATE (chat_id, geracao, atualizado_em)
    ON public.chat_sessao TO giom_pipeline;

-- ---------- 7. Sequences (INSERT com DEFAULT nextval) -------------------------------------
-- serial/identity: sem USAGE na sequence o INSERT falha com "permission denied for sequence".
DO $$
DECLARE
    v_tabela   text;
    v_sequence text;
BEGIN
    FOREACH v_tabela IN ARRAY ARRAY['transcricao', 'resumo', 'categoria_teologica', 'video_chunk', 'conversa_historico', 'interacao_log']
    LOOP
        v_sequence := pg_get_serial_sequence('public.' || v_tabela, 'id');
        IF v_sequence IS NULL THEN
            RAISE WARNING 'public.%.id não tem sequence (uuid/default próprio?) — nada a conceder', v_tabela;
        ELSE
            EXECUTE format('GRANT USAGE, SELECT ON SEQUENCE %s TO giom_pipeline', v_sequence);
        END IF;
    END LOOP;
END $$;

-- ---------- 8. RLS ----------------------------------------------------------------------
-- Com RLS ligado, o GRANT sozinho não basta: sem policy que cubra a role a tabela aparece
-- vazia / o INSERT é negado. As policies existentes são "leitura_publica_*" (SELECT p/ todos)
-- e "service_role_only_*"/"escrita_service_role_*" (auth.role() = 'service_role', que
-- giom_pipeline nunca satisfaz). Estas policies são ADITIVAS (permissivas, TO giom_pipeline):
-- não alteram o que anon/service_role veem. Em tabela com RLS desligado são inertes.
-- UPDATE/INSERT ... ON CONFLICT precisam das policies de SELECT + INSERT + UPDATE juntas.

-- video, transcricao, resumo, video_etapa_terminal, processamento_progresso, chat_sessao: S/I/U
DROP POLICY IF EXISTS giom_pipeline_select ON public.video;
CREATE POLICY giom_pipeline_select ON public.video FOR SELECT TO giom_pipeline USING (true);
DROP POLICY IF EXISTS giom_pipeline_insert ON public.video;
CREATE POLICY giom_pipeline_insert ON public.video FOR INSERT TO giom_pipeline WITH CHECK (true);
DROP POLICY IF EXISTS giom_pipeline_update ON public.video;
CREATE POLICY giom_pipeline_update ON public.video FOR UPDATE TO giom_pipeline USING (true) WITH CHECK (true);

DROP POLICY IF EXISTS giom_pipeline_select ON public.transcricao;
CREATE POLICY giom_pipeline_select ON public.transcricao FOR SELECT TO giom_pipeline USING (true);
DROP POLICY IF EXISTS giom_pipeline_insert ON public.transcricao;
CREATE POLICY giom_pipeline_insert ON public.transcricao FOR INSERT TO giom_pipeline WITH CHECK (true);
DROP POLICY IF EXISTS giom_pipeline_update ON public.transcricao;
CREATE POLICY giom_pipeline_update ON public.transcricao FOR UPDATE TO giom_pipeline USING (true) WITH CHECK (true);

DROP POLICY IF EXISTS giom_pipeline_select ON public.resumo;
CREATE POLICY giom_pipeline_select ON public.resumo FOR SELECT TO giom_pipeline USING (true);
DROP POLICY IF EXISTS giom_pipeline_insert ON public.resumo;
CREATE POLICY giom_pipeline_insert ON public.resumo FOR INSERT TO giom_pipeline WITH CHECK (true);
DROP POLICY IF EXISTS giom_pipeline_update ON public.resumo;
CREATE POLICY giom_pipeline_update ON public.resumo FOR UPDATE TO giom_pipeline USING (true) WITH CHECK (true);

DROP POLICY IF EXISTS giom_pipeline_select ON public.video_etapa_terminal;
CREATE POLICY giom_pipeline_select ON public.video_etapa_terminal FOR SELECT TO giom_pipeline USING (true);
DROP POLICY IF EXISTS giom_pipeline_insert ON public.video_etapa_terminal;
CREATE POLICY giom_pipeline_insert ON public.video_etapa_terminal FOR INSERT TO giom_pipeline WITH CHECK (true);
DROP POLICY IF EXISTS giom_pipeline_update ON public.video_etapa_terminal;
CREATE POLICY giom_pipeline_update ON public.video_etapa_terminal FOR UPDATE TO giom_pipeline USING (true) WITH CHECK (true);

DROP POLICY IF EXISTS giom_pipeline_select ON public.processamento_progresso;
CREATE POLICY giom_pipeline_select ON public.processamento_progresso FOR SELECT TO giom_pipeline USING (true);
DROP POLICY IF EXISTS giom_pipeline_insert ON public.processamento_progresso;
CREATE POLICY giom_pipeline_insert ON public.processamento_progresso FOR INSERT TO giom_pipeline WITH CHECK (true);
DROP POLICY IF EXISTS giom_pipeline_update ON public.processamento_progresso;
CREATE POLICY giom_pipeline_update ON public.processamento_progresso FOR UPDATE TO giom_pipeline USING (true) WITH CHECK (true);

DROP POLICY IF EXISTS giom_pipeline_select ON public.chat_sessao;
CREATE POLICY giom_pipeline_select ON public.chat_sessao FOR SELECT TO giom_pipeline USING (true);
DROP POLICY IF EXISTS giom_pipeline_insert ON public.chat_sessao;
CREATE POLICY giom_pipeline_insert ON public.chat_sessao FOR INSERT TO giom_pipeline WITH CHECK (true);
DROP POLICY IF EXISTS giom_pipeline_update ON public.chat_sessao;
CREATE POLICY giom_pipeline_update ON public.chat_sessao FOR UPDATE TO giom_pipeline USING (true) WITH CHECK (true);

DROP POLICY IF EXISTS giom_pipeline_select ON public.conversa_historico;
CREATE POLICY giom_pipeline_select ON public.conversa_historico FOR SELECT TO giom_pipeline USING (true);
DROP POLICY IF EXISTS giom_pipeline_insert ON public.conversa_historico;
CREATE POLICY giom_pipeline_insert ON public.conversa_historico FOR INSERT TO giom_pipeline WITH CHECK (true);
DROP POLICY IF EXISTS giom_pipeline_update ON public.conversa_historico;
CREATE POLICY giom_pipeline_update ON public.conversa_historico FOR UPDATE TO giom_pipeline USING (true) WITH CHECK (true);

-- categoria_teologica e video_chunk: S/I/U/D (o código apaga e reinsere)
DROP POLICY IF EXISTS giom_pipeline_select ON public.categoria_teologica;
CREATE POLICY giom_pipeline_select ON public.categoria_teologica FOR SELECT TO giom_pipeline USING (true);
DROP POLICY IF EXISTS giom_pipeline_insert ON public.categoria_teologica;
CREATE POLICY giom_pipeline_insert ON public.categoria_teologica FOR INSERT TO giom_pipeline WITH CHECK (true);
DROP POLICY IF EXISTS giom_pipeline_update ON public.categoria_teologica;
CREATE POLICY giom_pipeline_update ON public.categoria_teologica FOR UPDATE TO giom_pipeline USING (true) WITH CHECK (true);
DROP POLICY IF EXISTS giom_pipeline_delete ON public.categoria_teologica;
CREATE POLICY giom_pipeline_delete ON public.categoria_teologica FOR DELETE TO giom_pipeline USING (true);

DROP POLICY IF EXISTS giom_pipeline_select ON public.video_chunk;
CREATE POLICY giom_pipeline_select ON public.video_chunk FOR SELECT TO giom_pipeline USING (true);
DROP POLICY IF EXISTS giom_pipeline_insert ON public.video_chunk;
CREATE POLICY giom_pipeline_insert ON public.video_chunk FOR INSERT TO giom_pipeline WITH CHECK (true);
DROP POLICY IF EXISTS giom_pipeline_update ON public.video_chunk;
CREATE POLICY giom_pipeline_update ON public.video_chunk FOR UPDATE TO giom_pipeline USING (true) WITH CHECK (true);
DROP POLICY IF EXISTS giom_pipeline_delete ON public.video_chunk;
CREATE POLICY giom_pipeline_delete ON public.video_chunk FOR DELETE TO giom_pipeline USING (true);

-- somente leitura: capitulo, etapa_provedor_config, provedor_preco
DROP POLICY IF EXISTS giom_pipeline_select ON public.capitulo;
CREATE POLICY giom_pipeline_select ON public.capitulo FOR SELECT TO giom_pipeline USING (true);
DROP POLICY IF EXISTS giom_pipeline_select ON public.etapa_provedor_config;
CREATE POLICY giom_pipeline_select ON public.etapa_provedor_config FOR SELECT TO giom_pipeline USING (true);
DROP POLICY IF EXISTS giom_pipeline_select ON public.provedor_preco;
CREATE POLICY giom_pipeline_select ON public.provedor_preco FOR SELECT TO giom_pipeline USING (true);

-- canal_ingestao: leitura só dos canais ativos (o código sempre filtra ativo=eq.true)
DROP POLICY IF EXISTS giom_pipeline_select ON public.canal_ingestao;
CREATE POLICY giom_pipeline_select ON public.canal_ingestao FOR SELECT TO giom_pipeline USING (ativo);

-- provedor_rate_limit: SELECT + UPDATE (farol_consumir trava e debita o saldo)
DROP POLICY IF EXISTS giom_pipeline_select ON public.provedor_rate_limit;
CREATE POLICY giom_pipeline_select ON public.provedor_rate_limit FOR SELECT TO giom_pipeline USING (true);
DROP POLICY IF EXISTS giom_pipeline_update ON public.provedor_rate_limit;
CREATE POLICY giom_pipeline_update ON public.provedor_rate_limit FOR UPDATE TO giom_pipeline USING (true) WITH CHECK (true);

-- interacao_log: só INSERT
DROP POLICY IF EXISTS giom_pipeline_insert ON public.interacao_log;
CREATE POLICY giom_pipeline_insert ON public.interacao_log FOR INSERT TO giom_pipeline WITH CHECK (true);

-- ---------- 9. Autoverificação: proibições (falha => ROLLBACK de tudo) ----------------------
DO $$
BEGIN
    IF to_regclass('public.app_secret') IS NOT NULL AND (
           has_table_privilege('giom_pipeline', 'public.app_secret', 'SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER')
        OR has_any_column_privilege('giom_pipeline', 'public.app_secret', 'SELECT, INSERT, UPDATE, REFERENCES')) THEN
        RAISE EXCEPTION 'giom_pipeline tem acesso a app_secret (direto ou herdado de PUBLIC) — proibido; revogue o grant de PUBLIC/default privileges e rode de novo';
    END IF;

    IF has_table_privilege('giom_pipeline', 'public.canal_ingestao', 'INSERT, UPDATE, DELETE, TRUNCATE')
        OR has_any_column_privilege('giom_pipeline', 'public.canal_ingestao', 'INSERT, UPDATE') THEN
        RAISE EXCEPTION 'giom_pipeline pode escrever em canal_ingestao (direto ou herdado de PUBLIC) — proibido';
    END IF;

    IF has_schema_privilege('giom_pipeline', 'public', 'CREATE') THEN
        RAISE WARNING 'giom_pipeline herda CREATE no schema public de PUBLIC (Postgres < 15) — recomendado: REVOKE CREATE ON SCHEMA public FROM PUBLIC';
    END IF;
END $$;

COMMIT;
