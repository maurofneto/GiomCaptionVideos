-- =========================================================
-- Bug #111: PipelineHandler marcava video.processed_at assim que QUALQUER etapa terminal (lista
-- vazia na topologia) terminava — em canais com fan-out real (ex: Instituto:
-- transcricao -> [resumo, embedding], onde embedding -> [] E resumo -> categorizacao -> [] são
-- DUAS folhas terminais separadas), o primeiro ramo a terminar já marcava o vídeo como concluído,
-- mesmo com o outro ramo ainda rodando.
--
-- Esta tabela rastreia, por vídeo, quais etapas terminais (da topologia daquele canal) já foram
-- concluídas. O PipelineHandler faz upsert aqui a cada etapa terminal recebida e só marca
-- processed_at quando o conjunto de etapas registradas cobre TODAS as etapas terminais da
-- topologia do canal (calculado em runtime a partir de Topologia:Topologia, não hardcoded aqui).
-- =========================================================

CREATE TABLE IF NOT EXISTS video_etapa_terminal (
    video_id      TEXT        NOT NULL REFERENCES video(id) ON DELETE CASCADE,
    etapa         TEXT        NOT NULL,
    concluido_em  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (video_id, etapa)
);

ALTER TABLE video_etapa_terminal ENABLE ROW LEVEL SECURITY;

-- Igual a canal_ingestao/etapa_provedor_config — dado operacional, só o worker (service_role) lê/escreve.
CREATE POLICY "service_role_only_video_etapa_terminal" ON video_etapa_terminal
    FOR ALL USING (auth.role() = 'service_role');
