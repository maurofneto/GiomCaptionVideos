-- =========================================================
-- Estende etapa_provedor_config (US #106) pras etapas de Embedding e Transcrição (US #129) —
-- antes só resumo/categorizacao tinham cadeia de fallback por canal via este mecanismo; Embedding
-- e Transcrição ainda resolviam a cadeia uma vez, no boot do DI, a partir do FallbackChain fixo
-- em appsettings.json. A refatoração de código (FallbackEmbeddingService/PipelineEmbeddingService/
-- FallbackCaptionService passam a resolver por video.ChannelId em runtime, mesmo padrão de
-- FallbackResumoService) é o que efetivamente usa essas linhas — a migration sozinha não muda
-- comportamento nenhum até o código ser trocado.
--
-- prompt vira NULLABLE: Embedding não usa prompt (vetorização direta, não geração via LLM) —
-- Transcrição também não usa (é extração de legenda/áudio, não geração via LLM). Nenhuma linha
-- nova aqui preenche prompt.
--
-- Seed replica a ordem hoje fixa em appsettings.json de cada worker, sem mudar comportamento no
-- cutover:
--   - Transcricao:FallbackChain = [YouTubeCaption, YouTubeTranscript, YtDlpCaption, Whisper] —
--     mesma lista pros dois canais (unificado desde a Feature #104, 1 worker só pros 2 canais).
--   - Embedding:FallbackChain — só Instituto tem essa etapa na topologia
--     (Orquestracao.Worker/appsettings.json: Ministério é terminal em resumo, nunca chega em
--     embedding). Migration roda igual em dev e prod, mas as duas tinham valores DIFERENTES:
--     dev já tinha [OpenAI, VoyageAI, Cohere] ativos (Feature #115/US #117/#118/#119, validação
--     multi-provedor em andamento numa branch paralela), prod só [OpenAI]. Semeando os 3 nos
--     dois ambientes — replica o que já está ativo em dev hoje (não regride a validação em
--     andamento) e antecipa em prod um dado que o #119 ligaria de qualquer forma; PipelineEmbeddingService
--     roda todos os providers da cadeia (não é "fallback só no primeiro"), então ter os 3 sempre
--     rodou em dev sem problema — não é comportamento novo pra esse ambiente.
-- =========================================================

ALTER TABLE etapa_provedor_config ALTER COLUMN prompt DROP NOT NULL;

ALTER TABLE etapa_provedor_config DROP CONSTRAINT etapa_provedor_config_etapa_check;
ALTER TABLE etapa_provedor_config ADD CONSTRAINT etapa_provedor_config_etapa_check
    CHECK (etapa IN ('resumo', 'categorizacao', 'embedding', 'transcricao'));

ALTER TABLE etapa_provedor_config DROP CONSTRAINT etapa_provedor_config_provedor_check;
ALTER TABLE etapa_provedor_config ADD CONSTRAINT etapa_provedor_config_provedor_check
    CHECK (provedor IN (
        'Claude', 'Grok', 'OpenAI', 'Kimi',
        'VoyageAI', 'Cohere',
        'YouTubeCaption', 'YouTubeTranscript', 'YtDlpCaption', 'Whisper'
    ));

INSERT INTO etapa_provedor_config (channel_id, etapa, provedor, ordem, prompt, usar_observabilidade) VALUES
-- Instituto Giom — transcricao (YouTubeCaption -> YouTubeTranscript -> YtDlpCaption -> Whisper)
('UCyyXFx2xHdzfavx6H6Q8zJw', 'transcricao', 'YouTubeCaption', 1, NULL, false),
('UCyyXFx2xHdzfavx6H6Q8zJw', 'transcricao', 'YouTubeTranscript', 2, NULL, false),
('UCyyXFx2xHdzfavx6H6Q8zJw', 'transcricao', 'YtDlpCaption', 3, NULL, false),
('UCyyXFx2xHdzfavx6H6Q8zJw', 'transcricao', 'Whisper', 4, NULL, false),

-- Ministério Giom — transcricao (mesma cadeia, unificada desde a Feature #104)
('UCyHMyZH5oon8jW7y_yMRstQ', 'transcricao', 'YouTubeCaption', 1, NULL, false),
('UCyHMyZH5oon8jW7y_yMRstQ', 'transcricao', 'YouTubeTranscript', 2, NULL, false),
('UCyHMyZH5oon8jW7y_yMRstQ', 'transcricao', 'YtDlpCaption', 3, NULL, false),
('UCyHMyZH5oon8jW7y_yMRstQ', 'transcricao', 'Whisper', 4, NULL, false),

-- Instituto Giom — embedding (Ministério não tem essa etapa na topologia). PipelineEmbeddingService
-- roda TODOS os 3 (indexação, não é fallback-para-no-primeiro) — Ordem só importa mesmo pro
-- FallbackEmbeddingService (busca do bot), que hoje só usa OpenAI de qualquer forma.
('UCyyXFx2xHdzfavx6H6Q8zJw', 'embedding', 'OpenAI', 1, NULL, false),
('UCyyXFx2xHdzfavx6H6Q8zJw', 'embedding', 'VoyageAI', 2, NULL, false),
('UCyyXFx2xHdzfavx6H6Q8zJw', 'embedding', 'Cohere', 3, NULL, false)
ON CONFLICT (channel_id, etapa, provedor) DO NOTHING;
