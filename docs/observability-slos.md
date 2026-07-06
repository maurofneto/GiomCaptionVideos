# SLOs do bot Timóteo IA (Instituto Giom)

Metas numéricas para julgar objetivamente se o bot está bom ou ruim, calculadas sobre uma
janela móvel dos últimos 7 dias a partir da telemetria já gravada (`interacao_log` +
`v_feedback_bot`). Consultável a qualquer momento via `select * from v_slo_bot;` (Supabase,
prod e dev).

| SLO | Métrica | Alvo | Fonte |
|---|---|---|---|
| Latência | p95 do round-trip completo (busca RAG + Claude + envio) | < 15s | `interacao_log` tipo `bot_interacao` |
| Cobertura RAG | % de buscas sem nenhum chunk acima do threshold | < 15% | `interacao_log` tipo `busca_rag`, `detalhes->>'chunks' = '0'` |
| Confiabilidade da resposta | % de respostas em que o bot admite incerteza | < 10% | `interacao_log` tipo `bot_resposta`, `detalhes->>'incerteza' = 'true'` |
| Satisfação | % de respostas avaliadas como úteis (👍) | > 80% | `v_feedback_bot` (feedback do usuário) |

## Origem dos valores

Os alvos acima são um ponto de partida (não medidos ainda contra histórico real) — ajustar
depois de observar `v_slo_bot` por algumas semanas de uso real.

## View

Definida na migration `create_view_slo_bot` (aplicada em prod `rhjsjfbegtusucunkgav` e dev
`fajrstyqgogmqzceujjt`). Cada SLO vem pareado com o próprio alvo (`_alvo_` no nome da coluna)
para comparação direta na mesma query, sem precisar manter os números sincronizados em dois
lugares.
