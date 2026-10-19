-- DOWN de 20261019130100: remove a linha xAI e a coluna por_hora (reverter o código antes).
DELETE FROM provedor_preco WHERE provedor = 'xAI' AND modelo = 'grok-voice-transcribe-2.0';
ALTER TABLE provedor_preco DROP COLUMN IF EXISTS por_hora;
