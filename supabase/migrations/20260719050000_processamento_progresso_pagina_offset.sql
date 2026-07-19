-- Bug #125/#126: MaxVideosPerRun é um teto DURO (rate limit real do YouTube/downstream) — ao
-- bater no meio de uma página, a execução não pode nem processar 1 vídeo a mais (estouraria o
-- teto) nem avançar pra próxima página (perderia os vídeos que sobraram da atual pra sempre,
-- ver Bug #125). A correção certa é retomar a MESMA página exatamente de onde parou — o que
-- exige saber não só QUAL página (last_page_token), mas TAMBÉM qual foi o último índice
-- avaliado dentro dela.

ALTER TABLE processamento_progresso
    ADD COLUMN IF NOT EXISTS pagina_offset INTEGER NOT NULL DEFAULT 0;

COMMENT ON COLUMN processamento_progresso.pagina_offset IS 'Índice do primeiro vídeo ainda não avaliado dentro da página apontada por last_page_token (Bug #125/#126) — 0 quando a página é nova/foi totalmente percorrida.';
