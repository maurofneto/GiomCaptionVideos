-- Kimi K3 (US #124) já é um elo real de Resumo/Categorização (Infrastructure.Kimi), mas
-- etapa_provedor_config_provedor_check só permitia 'Claude'/'Grok'/'OpenAI' — nunca foi
-- atualizado quando o Kimi entrou. Sem isso, nenhuma linha com provedor='Kimi' pode ser
-- inserida (nem em dev pra teste, nem futuramente em prod).

ALTER TABLE etapa_provedor_config DROP CONSTRAINT etapa_provedor_config_provedor_check;
ALTER TABLE etapa_provedor_config ADD CONSTRAINT etapa_provedor_config_provedor_check
    CHECK (provedor = ANY (ARRAY['Claude'::text, 'Grok'::text, 'OpenAI'::text, 'Kimi'::text]));
