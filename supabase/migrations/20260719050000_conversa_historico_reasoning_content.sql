-- US #126: Kimi K3 ("thinking" sempre ligado) separa reasoning_content de content na resposta,
-- e exige preservar/reenviar o raciocínio nas próximas chamadas da mesma conversa (exigência da
-- API deles, não escolha nossa) — TurnoConversa (Domain) ganhou esse campo, e ele precisa de
-- lugar pra viver na tabela. Null pra todo turno gerado por Claude/Grok/OpenAI.

ALTER TABLE conversa_historico ADD COLUMN IF NOT EXISTS reasoning_content TEXT;
