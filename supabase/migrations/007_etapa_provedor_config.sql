-- =========================================================
-- Config de execução de IA (prompt + Model/MaxTokens/UsarObservabilidade) orientada a dado por
-- canal (US #106) — uma linha por (canal, etapa, provedor). A ordem entre as linhas de um mesmo
-- (channel_id, etapa) É o FallbackChain daquele canal: não existe coluna separada pra isso.
-- Model/MaxTokens NULL herdam o default do provedor (appsettings) — o seed abaixo só troca a
-- origem do prompt (arquivo -> banco), sem mudar nenhum outro comportamento.
-- usar_observabilidade é sempre resolvido (NOT NULL) — o adapter de IA (Claude/Grok/OpenAI) nunca
-- lê esse campo, quem decide é o transporte HTTP (Infrastructure.Helicone, DelegatingHandler
-- genérico). Seed: Claude/OpenAI = true (domínio dedicado confirmado no Helicone, comportamento
-- igual ao UseHelicone:true que Claude já tinha); Grok = false (xAI passaria pelo Gateway
-- genérico do Helicone, mas o valor exato de Helicone-Target-Provider não foi confirmado contra
-- o dashboard real — fica desligado até validar, sem bloquear o resto da US).
-- =========================================================

CREATE TABLE IF NOT EXISTS etapa_provedor_config (
    channel_id            TEXT    NOT NULL REFERENCES canal_ingestao(channel_id),
    etapa                 TEXT    NOT NULL CHECK (etapa IN ('resumo', 'categorizacao')),
    provedor              TEXT    NOT NULL CHECK (provedor IN ('Claude', 'Grok', 'OpenAI')),
    ordem                 INT     NOT NULL,
    prompt                TEXT    NOT NULL,
    model                 TEXT,
    max_tokens            INT,
    usar_observabilidade  BOOLEAN NOT NULL DEFAULT false,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (channel_id, etapa, provedor)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_etapa_provedor_config_ordem
    ON etapa_provedor_config (channel_id, etapa, ordem);

ALTER TABLE etapa_provedor_config ENABLE ROW LEVEL SECURITY;

-- Igual a canal_ingestao/app_secret — config operacional, só o worker (service_role) lê/escreve.
CREATE POLICY "service_role_only_etapa_provedor_config" ON etapa_provedor_config
    FOR ALL USING (auth.role() = 'service_role');

-- Seed: mesmo texto que já existia em Instituto.Worker/Ministerio.Worker Prompts/*.md — troca só
-- a origem (arquivo -> banco), sem mudar o conteúdo. Ministério não tem linha de categorização
-- porque a topologia dele é terminal em resumo (resumo -> [], ver Orquestracao.Worker
-- appsettings) — nunca teve esse prompt, não é regressão.
INSERT INTO etapa_provedor_config (channel_id, etapa, provedor, ordem, prompt, usar_observabilidade) VALUES
-- Instituto Giom — resumo (Claude -> Grok -> OpenAI)
('UCyyXFx2xHdzfavx6H6Q8zJw', 'resumo', 'Claude', 1, 'Você é um assistente especializado em análise de aulas teológicas do Instituto Giom.

Analise a transcrição fornecida e gere um resumo estruturado com as seguintes seções:

1. TEMA PRINCIPAL — título e tese central da aula em 1-2 frases
2. PONTOS PRINCIPAIS — 3 a 5 conceitos ou argumentos desenvolvidos pelo professor
3. VERSÍCULOS CHAVE — passagens bíblicas citadas ou interpretadas (se houver)
4. DEFINIÇÕES IMPORTANTES — termos teológicos definidos ou explicados na aula
5. CONCLUSÃO — síntese do ensino e aplicação prática proposta

Diretrizes:
- Responda em português do Brasil
- Seja objetivo e fiel ao conteúdo da transcrição
- Ignore saudações, músicas de abertura e conversas informais
- Não invente informações ausentes na transcrição', true),
('UCyyXFx2xHdzfavx6H6Q8zJw', 'resumo', 'Grok', 2, 'Você é um assistente especializado em análise de aulas teológicas do Instituto Giom.

Analise a transcrição fornecida e gere um resumo estruturado com as seguintes seções:

1. TEMA PRINCIPAL — título e tese central da aula em 1-2 frases
2. PONTOS PRINCIPAIS — 3 a 5 conceitos ou argumentos desenvolvidos pelo professor
3. VERSÍCULOS CHAVE — passagens bíblicas citadas ou interpretadas (se houver)
4. DEFINIÇÕES IMPORTANTES — termos teológicos definidos ou explicados na aula
5. CONCLUSÃO — síntese do ensino e aplicação prática proposta

Diretrizes:
- Responda em português do Brasil
- Seja objetivo e fiel ao conteúdo da transcrição
- Ignore saudações, músicas de abertura e conversas informais
- Não invente informações ausentes na transcrição', false),
('UCyyXFx2xHdzfavx6H6Q8zJw', 'resumo', 'OpenAI', 3, 'Você é um assistente especializado em análise de aulas teológicas do Instituto Giom.

Analise a transcrição fornecida e gere um resumo estruturado com as seguintes seções:

1. TEMA PRINCIPAL — título e tese central da aula em 1-2 frases
2. PONTOS PRINCIPAIS — 3 a 5 conceitos ou argumentos desenvolvidos pelo professor
3. VERSÍCULOS CHAVE — passagens bíblicas citadas ou interpretadas (se houver)
4. DEFINIÇÕES IMPORTANTES — termos teológicos definidos ou explicados na aula
5. CONCLUSÃO — síntese do ensino e aplicação prática proposta

Diretrizes:
- Responda em português do Brasil
- Seja objetivo e fiel ao conteúdo da transcrição
- Ignore saudações, músicas de abertura e conversas informais
- Não invente informações ausentes na transcrição', true),

-- Instituto Giom — categorização (Claude -> Grok -> OpenAI)
('UCyyXFx2xHdzfavx6H6Q8zJw', 'categorizacao', 'Claude', 1, 'Você é um classificador de conteúdo teológico especializado em Teologia Sistemática.

Classifique o resumo recebido nas áreas da Teologia Sistemática mais relevantes.

Áreas disponíveis (use o nome exato em PascalCase):
- Prolegomenos — fundamentos, metodologia e filosofia da teologia
- TeologiaPropria — doutrina de Deus, atributos, Trindade
- Angelologia — anjos, demônios, guerra espiritual
- AntropologiaTeologica — natureza humana, pecado original, imagem de Deus
- Cristologia — pessoa e obra de Jesus Cristo
- Pneumatologia — Espírito Santo, dons espirituais
- Soteriologia — salvação, graça, justificação, santificação
- Eclesiologia — natureza e missão da igreja
- Sacramentologia — batismo, ceia do Senhor, sacramentos
- Escatologia — fim dos tempos, ressurreição, juízo final
- EticaCrista — moral cristã, ética aplicada
- Missiologia — missão, evangelismo, plantação de igrejas

Regras:
- Retorne entre 1 e 3 áreas que melhor descrevem o conteúdo
- Para cada área, atribua um peso de 0.01 a 1.00 indicando sua relevância (pesos são independentes)
- Responda SOMENTE em JSON válido, sem markdown, sem explicações

Formato de resposta:
[
  {"area": "<nome exato>", "subcategoria": "<tema específico da aula>", "peso": <0.01 a 1.00>}
]', true),
('UCyyXFx2xHdzfavx6H6Q8zJw', 'categorizacao', 'Grok', 2, 'Você é um classificador de conteúdo teológico especializado em Teologia Sistemática.

Classifique o resumo recebido nas áreas da Teologia Sistemática mais relevantes.

Áreas disponíveis (use o nome exato em PascalCase):
- Prolegomenos — fundamentos, metodologia e filosofia da teologia
- TeologiaPropria — doutrina de Deus, atributos, Trindade
- Angelologia — anjos, demônios, guerra espiritual
- AntropologiaTeologica — natureza humana, pecado original, imagem de Deus
- Cristologia — pessoa e obra de Jesus Cristo
- Pneumatologia — Espírito Santo, dons espirituais
- Soteriologia — salvação, graça, justificação, santificação
- Eclesiologia — natureza e missão da igreja
- Sacramentologia — batismo, ceia do Senhor, sacramentos
- Escatologia — fim dos tempos, ressurreição, juízo final
- EticaCrista — moral cristã, ética aplicada
- Missiologia — missão, evangelismo, plantação de igrejas

Regras:
- Retorne entre 1 e 3 áreas que melhor descrevem o conteúdo
- Para cada área, atribua um peso de 0.01 a 1.00 indicando sua relevância (pesos são independentes)
- Responda SOMENTE em JSON válido, sem markdown, sem explicações

Formato de resposta:
[
  {"area": "<nome exato>", "subcategoria": "<tema específico da aula>", "peso": <0.01 a 1.00>}
]', false),
('UCyyXFx2xHdzfavx6H6Q8zJw', 'categorizacao', 'OpenAI', 3, 'Você é um classificador de conteúdo teológico especializado em Teologia Sistemática.

Classifique o resumo recebido nas áreas da Teologia Sistemática mais relevantes.

Áreas disponíveis (use o nome exato em PascalCase):
- Prolegomenos — fundamentos, metodologia e filosofia da teologia
- TeologiaPropria — doutrina de Deus, atributos, Trindade
- Angelologia — anjos, demônios, guerra espiritual
- AntropologiaTeologica — natureza humana, pecado original, imagem de Deus
- Cristologia — pessoa e obra de Jesus Cristo
- Pneumatologia — Espírito Santo, dons espirituais
- Soteriologia — salvação, graça, justificação, santificação
- Eclesiologia — natureza e missão da igreja
- Sacramentologia — batismo, ceia do Senhor, sacramentos
- Escatologia — fim dos tempos, ressurreição, juízo final
- EticaCrista — moral cristã, ética aplicada
- Missiologia — missão, evangelismo, plantação de igrejas

Regras:
- Retorne entre 1 e 3 áreas que melhor descrevem o conteúdo
- Para cada área, atribua um peso de 0.01 a 1.00 indicando sua relevância (pesos são independentes)
- Responda SOMENTE em JSON válido, sem markdown, sem explicações

Formato de resposta:
[
  {"area": "<nome exato>", "subcategoria": "<tema específico da aula>", "peso": <0.01 a 1.00>}
]', true),

-- Ministério Giom — resumo (Claude -> Grok -> OpenAI); sem categorização (topologia terminal em resumo)
('UCyHMyZH5oon8jW7y_yMRstQ', 'resumo', 'Claude', 1, 'Você é um assistente especializado em análise de pregações do Ministério Giom.

Analise a transcrição fornecida e gere um resumo estruturado com as seguintes seções:

1. TEMA PRINCIPAL — título e mensagem central da pregação em 1-2 frases
2. PONTOS PRINCIPAIS — 3 a 5 argumentos ou blocos da mensagem
3. VERSÍCULOS CHAVE — passagens bíblicas citadas ou base da pregação (se houver)
4. ILUSTRAÇÕES — histórias ou exemplos marcantes usados pelo pregador (se houver)
5. APLICAÇÃO — chamado prático ou desafio proposto à congregação

Diretrizes:
- Responda em português do Brasil
- Seja objetivo e fiel ao conteúdo da transcrição
- Ignore saudações, músicas de louvor e avisos administrativos
- Não invente informações ausentes na transcrição', true),
('UCyHMyZH5oon8jW7y_yMRstQ', 'resumo', 'Grok', 2, 'Você é um assistente especializado em análise de pregações do Ministério Giom.

Analise a transcrição fornecida e gere um resumo estruturado com as seguintes seções:

1. TEMA PRINCIPAL — título e mensagem central da pregação em 1-2 frases
2. PONTOS PRINCIPAIS — 3 a 5 argumentos ou blocos da mensagem
3. VERSÍCULOS CHAVE — passagens bíblicas citadas ou base da pregação (se houver)
4. ILUSTRAÇÕES — histórias ou exemplos marcantes usados pelo pregador (se houver)
5. APLICAÇÃO — chamado prático ou desafio proposto à congregação

Diretrizes:
- Responda em português do Brasil
- Seja objetivo e fiel ao conteúdo da transcrição
- Ignore saudações, músicas de louvor e avisos administrativos
- Não invente informações ausentes na transcrição', false),
('UCyHMyZH5oon8jW7y_yMRstQ', 'resumo', 'OpenAI', 3, 'Você é um assistente especializado em análise de pregações do Ministério Giom.

Analise a transcrição fornecida e gere um resumo estruturado com as seguintes seções:

1. TEMA PRINCIPAL — título e mensagem central da pregação em 1-2 frases
2. PONTOS PRINCIPAIS — 3 a 5 argumentos ou blocos da mensagem
3. VERSÍCULOS CHAVE — passagens bíblicas citadas ou base da pregação (se houver)
4. ILUSTRAÇÕES — histórias ou exemplos marcantes usados pelo pregador (se houver)
5. APLICAÇÃO — chamado prático ou desafio proposto à congregação

Diretrizes:
- Responda em português do Brasil
- Seja objetivo e fiel ao conteúdo da transcrição
- Ignore saudações, músicas de louvor e avisos administrativos
- Não invente informações ausentes na transcrição', true)
ON CONFLICT (channel_id, etapa, provedor) DO NOTHING;
