-- =========================================================
-- GrokVoice (xAI STT) entra na cadeia de transcrição de TODOS os canais que têm Whisper, logo antes
-- dele (Config First: a ordem da cadeia é dado, não código — ver docs/CONFIG_FIRST.md e
-- docs/runbook-grokvoice.md). GrokVoice assume a posição do Whisper e o Whisper vai para o fim.
-- Idempotente: canal que já tem GrokVoice (ex.: dev, inserido à mão) é ignorado.
--
-- ATENÇÃO: o elo só funciona com a chave /giom/{env}/pipeline/GrokVoice/ApiKey no SSM. Sem ela a
-- chamada falha e a cadeia segue para o Whisper (comportamento atual) — não quebra nada.
-- =========================================================

DO $$
DECLARE
    r RECORD;
    fim INT;
BEGIN
    FOR r IN
        SELECT w.channel_id, w.ordem AS ordem_whisper
          FROM etapa_provedor_config w
         WHERE w.etapa = 'transcricao'
           AND w.provedor = 'Whisper'
           AND NOT EXISTS (SELECT 1 FROM etapa_provedor_config g
                            WHERE g.channel_id = w.channel_id AND g.etapa = 'transcricao' AND g.provedor = 'GrokVoice')
    LOOP
        SELECT MAX(ordem) + 1 INTO fim FROM etapa_provedor_config
         WHERE channel_id = r.channel_id AND etapa = 'transcricao';

        UPDATE etapa_provedor_config SET ordem = fim, updated_at = NOW()
         WHERE channel_id = r.channel_id AND etapa = 'transcricao' AND provedor = 'Whisper';

        INSERT INTO etapa_provedor_config (channel_id, etapa, provedor, ordem, prompt, usar_observabilidade)
        VALUES (r.channel_id, 'transcricao', 'GrokVoice', r.ordem_whisper, NULL, false);
    END LOOP;
END $$;
