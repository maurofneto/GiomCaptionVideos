-- US #148 — Grok Voice (xAI speech-to-text) como elo da cadeia de Transcrição.
--
-- Esta migration SÓ amplia o CHECK de etapa_provedor_config.provedor pra aceitar 'GrokVoice'
-- (mesmo padrão da 20260720191143, que fez o mesmo pro Kimi). NÃO insere nenhuma linha de cadeia:
-- sem linha em etapa_provedor_config o GrokVoice não participa da transcrição de nenhum canal,
-- então rodar isto em prod não muda comportamento. A linha da cadeia entra primeiro só em DEV
-- (SQL no docs/runbook-grokvoice.md) e em prod só depois do teste validado em dev.
--
-- Idempotente: DROP ... IF EXISTS + ADD recria o mesmo conjunto + 'GrokVoice'.

ALTER TABLE etapa_provedor_config DROP CONSTRAINT IF EXISTS etapa_provedor_config_provedor_check;
ALTER TABLE etapa_provedor_config ADD CONSTRAINT etapa_provedor_config_provedor_check
    CHECK (provedor IN (
        'Claude', 'Grok', 'OpenAI', 'Kimi',
        'VoyageAI', 'Cohere',
        'YouTubeCaption', 'YouTubeTranscript', 'YtDlpCaption', 'GrokVoice', 'Whisper'
    ));
