# Adoção e retenção de usuários do bot (Instituto Giom)

Estende a memória de conversa persistida (Board #25) — como `conversa_historico` já grava
`chat_id` e `criado_em` por interação, dá para medir adoção (usuários novos) e retenção
(usuários que voltam) sem instrumentação adicional. Consultável via
`select * from v_adocao_retencao;` (Supabase, prod e dev).

| Coluna | Significado |
|---|---|
| `semana` | Início da semana (segunda-feira) |
| `usuarios_ativos` | Chat IDs distintos que interagiram naquela semana |
| `usuarios_novos` | Chat IDs cuja primeira interação de todas foi naquela semana |
| `usuarios_recorrentes` | Chat IDs que já eram ativos antes e voltaram naquela semana |
| `retencao_semana_anterior_pct` | % dos usuários ativos na semana anterior que também apareceram nesta semana |

## Leitura do valor atual (prod, 2026-07-06)

Só 4 dias de dados reais (2 usuários distintos, 25 interações) — os números ainda não são
representativos de tendência, é cedo para tirar conclusão de adoção/retenção. A view fica
pronta para acompanhar a partir de agora conforme o uso cresce.

## View

Definida na migration `create_view_adocao_retencao` (aplicada em prod `rhjsjfbegtusucunkgav`
e dev `fajrstyqgogmqzceujjt`). Granularidade semanal (`date_trunc('week', ...)`) — troca fácil
para diária se o volume justificar uma janela mais fina no futuro.
