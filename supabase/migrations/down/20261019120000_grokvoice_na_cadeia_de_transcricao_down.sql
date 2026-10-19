-- DOWN de 20261019120000_grokvoice_na_cadeia_de_transcricao.sql: tira o GrokVoice da cadeia de
-- transcrição e devolve o Whisper à posição logo depois do último elo restante.
DELETE FROM etapa_provedor_config WHERE etapa = 'transcricao' AND provedor = 'GrokVoice';

UPDATE etapa_provedor_config w
   SET ordem = COALESCE((SELECT MAX(o.ordem) FROM etapa_provedor_config o
                          WHERE o.channel_id = w.channel_id AND o.etapa = 'transcricao' AND o.provedor <> 'Whisper'), 0) + 1,
       updated_at = NOW()
 WHERE w.etapa = 'transcricao' AND w.provedor = 'Whisper';
