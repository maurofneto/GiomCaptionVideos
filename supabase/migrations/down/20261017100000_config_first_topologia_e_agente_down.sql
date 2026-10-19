-- DOWN de 20261017100000_config_first_topologia_e_agente.sql: remove as duas tabelas.
-- ATENÇÃO: o código que lê essas tabelas (Orquestracao.Worker, agentes do bot) precisa ser revertido
-- ANTES, senão o orquestrador falha por falta de topologia. Sem as tabelas o bot segue sem instrução
-- de concisão (prompt inalterado).
DROP TABLE IF EXISTS agente_config;
DROP TABLE IF EXISTS etapa_topologia;
