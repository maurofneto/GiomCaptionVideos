---
name: editar-dashboard-sentry
description: Cria ou edita widgets em dashboards do Sentry deste projeto via chamada direta à REST API — não existe ferramenta MCP para escrita de dashboard, só leitura. Use sempre que o usuário pedir pra "ajustar o dashboard", "adicionar um widget no Sentry", "criar um dashboard novo", "corrige esse gráfico do Sentry", ou mencionar métricas/gráficos que deveriam aparecer lá — mesmo sem citar "API" ou "REST" explicitamente.
---

# Editar dashboard do Sentry via API

Não existe ferramenta MCP pra escrever dashboard do Sentry (só leitura/busca). Toda edição
passa por `curl`/PowerShell direto na REST API.

## Token

Precisa de um token de API do Sentry (permissão de escrita em dashboards). Se o usuário ainda
não passou um, peça. **Nunca ecoe o valor em nenhuma resposta** — guarde só num arquivo de
scratchpad (fora do repo, na pasta de scratchpad da sessão) e use via substituição de shell
(`$(cat arquivo)`), nunca digitado literalmente num comando.

## Fluxo

1. **GET o dashboard atual** — sempre antes de editar, pra não perder widgets existentes:
   ```bash
   TOKEN=$(cat <scratchpad>/.sentry_token)
   curl -s -H "Authorization: Bearer $TOKEN" \
     "https://sentry.io/api/0/organizations/<org>/dashboards/<id>/" \
     -o <scratchpad>/dash.json
   ```

2. **Monte o payload do PUT** preservando os widgets existentes **com seus `id`** (e o `id` da
   query aninhada dentro de cada widget) — omitir o `id` de um widget existente faz o Sentry
   criar um novo em vez de atualizar. Widgets **novos** simplesmente não têm campo `id`.

3. **Forma de um widget**:
   ```json
   {
     "title": "...", "description": "", "displayType": "big_number|line|table",
     "interval": "1h",
     "queries": [{
       "name": "", "fields": ["count()"], "aggregates": ["count()"],
       "columns": [], "fieldAliases": [""], "conditions": "...", "orderby": ""
     }],
     "widgetType": "spans|tracemetrics|issue|error-events",
     "layout": {"x": 0, "y": 0, "w": 2, "h": 1, "minH": 1}
   }
   ```
   - `spans`: consultas sobre spans/transactions cruas (ex: `is_transaction:true
     transaction:bot.interacao`).
   - `tracemetrics`: métricas custom deste projeto (`giom.tempo_ms`, `giom.custo_usd`,
     `giom.tokens_entrada`, `giom.tokens_saida`), sintaxe de campo completa:
     `sum(value,giom.custo_usd,distribution,none)` ou
     `avg(value,giom.tempo_ms,distribution,millisecond)`, filtráveis pela tag `tipo`.
   - `issue` / `error-events`: dataset de issues/erros — atenção ao filtro de `environment`,
     que precisa de wildcard (`instituto*`) pra cobrir `instituto-dev`, `instituto-prod` etc.,
     nunca um valor exato sozinho.

4. **PUT pra atualizar** um dashboard existente, **POST** pra criar um novo:
   ```bash
   curl -s -X PUT -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
     --data-binary "@<scratchpad>/payload.json" \
     "https://sentry.io/api/0/organizations/<org>/dashboards/<id>/" \
     -w "\nHTTP_STATUS:%{http_code}\n"
   ```
   Confira `HTTP_STATUS:200` (update) ou `201` (create). Guarde os `id`s novos que a resposta
   devolve, se for continuar editando na mesma sessão.

## Dashboards existentes (referência)

- **"Giom - Observabilidade"**: latência/custo/tokens do bot e da pipeline.
- **"Giom - Erros"**: erros por ambiente (wildcard!).
- **"Giom - Saúde"**: visão de saúde 24h (vídeos processados/falhos, mensagens do bot, erros,
  gasto, runs da pipeline).
