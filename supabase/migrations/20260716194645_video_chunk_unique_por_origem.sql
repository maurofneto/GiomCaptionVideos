-- Bug #121: UNIQUE (video_id, chunk_index) não inclui origem. Com o DeletarPorVideoAsync
-- agora escopado por origem (fix no código, não neste arquivo), cada provedor do pipeline
-- multi-provedor (Feature #115) deixa de apagar os chunks dos outros — mas passa a tentar
-- inserir no mesmo chunk_index que outro provedor já ocupa pro mesmo vídeo, o que colide na
-- constraint atual. Correção: incluir origem na chave única, dando a cada provedor seu
-- próprio espaço de chunk_index por vídeo.
--
-- Dimensão da Voyage (voyage-3 = 1024 dim vs. vector(1536) fixo da coluna): tentativa de
-- tornar a coluna dimensionless (Option A, índices HNSW parciais por origem) falhou —
-- pgvector recusa HNSW sobre coluna sem dimensão fixa ("column does not have dimensions"),
-- mesmo com predicado parcial. Resolvido no código (VoyageEmbeddingService preenche o vetor
-- com zeros até 1536 antes de gravar/buscar — não distorce a similaridade de cosseno, e as
-- RPCs match_chunks_hibrido* já filtram por origem antes de comparar), sem mudança de schema.

alter table video_chunk drop constraint video_chunk_video_id_chunk_index_key;
alter table video_chunk add constraint video_chunk_video_id_chunk_index_origem_key
  unique (video_id, chunk_index, origem);
