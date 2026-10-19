-- =========================================================
-- Config First (US #152): preço por HORA de áudio (speech-to-text) em provedor_preco, ao lado do preço por
-- milhão de tokens. Antes o US$0,10/h do GrokVoice estava fixo no código (GrokVoiceOptions.PrecoUsdPorHora).
-- Fonte: docs.x.ai/developers/models/speech-to-text — REST (batch) US$ 0,10/h; streaming US$ 0,20/h.
-- =========================================================
ALTER TABLE provedor_preco ADD COLUMN IF NOT EXISTS por_hora NUMERIC(10,4);

INSERT INTO provedor_preco (provedor, modelo, entrada_por_milhao, saida_por_milhao, por_hora)
VALUES ('xAI', 'grok-voice-transcribe-2.0', 0, 0, 0.10)
ON CONFLICT (provedor, modelo) DO UPDATE SET por_hora = EXCLUDED.por_hora, updated_at = NOW();
