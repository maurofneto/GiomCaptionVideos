# Regra de relevância por canal

Define quais vídeos de um canal entram no pipeline. Fica em `canal_ingestao.regra_relevancia`
(JSON, obrigatório), é lida pelo `Ingestao.Worker` a cada execução e muda por `UPDATE`, sem deploy
(Config First). É a única definição de relevância: o enum antigo (`relevancia_tipo`,
`relevancia_valores`, `privacy_status_filtro`) não é mais lido.

## Formato

Um nó é um grupo ou uma condição:

```json
{ "todas":  [ nó, ... ] }                                  // E — grupo vazio vale verdadeiro
{ "alguma": [ nó, ... ] }                                  // OU — grupo vazio vale falso
{ "nao":    nó }                                           // negação
{ "campo": "titulo", "op": "comeca_com", "valor": "GIOM" } // condição
```

| Campo | Operadores | Valor |
|---|---|---|
| `titulo`, `descricao` | `contem`, `comeca_com`, `termina_com`, `igual`, `em` | texto (`em`: lista de textos) |
| `privacidade` | `igual`, `em` | `public`, `unlisted`, `private` |
| `tipo` | `igual`, `em` | `upload`, `live`, `short`, `premiere` |
| `transmissao` | `igual`, `em` | `nenhuma`, `agendada`, `ao_vivo`, `encerrada` |
| `duracao_segundos` | `igual`, `maior`, `maior_igual`, `menor`, `menor_igual`, `entre` | número (`entre`: `[de, até]`) |
| `enviado_em` | `antes`, `depois`, `entre` | data `yyyy-MM-dd` (`entre`: `[de, até]`, inclusive) |

A lista acima está no código, em `CatalogoRelevancia` (campos, operadores, valores permitidos e rótulos
em português). O parser valida contra ela, e um futuro Admin/API a expõe como está — não há arquivo
paralelo para manter.

- Texto não diferencia maiúsculas de minúsculas.
- Duração ou data de envio desconhecidas nunca casam (nem em `menor`/`antes`).
- Datas comparam por dia, ignorando a hora.
- As chaves podem vir em qualquer ordem (o `jsonb` do Postgres não preserva a ordem).
- Limites: até 8 níveis de aninhamento e 100 nós.

## Exemplos

Ministério hoje (título começa com GIOM e público):

```json
{ "todas": [
  { "alguma": [ { "campo": "titulo", "op": "comeca_com", "valor": "GIOM" } ] },
  { "campo": "privacidade", "op": "igual", "valor": "public" } ] }
```

Ignorar lives ainda não transmitidas e vídeos de menos de 5 minutos:

```json
{ "todas": [
  { "campo": "titulo", "op": "comeca_com", "valor": "GIOM" },
  { "nao": { "campo": "transmissao", "op": "em", "valor": ["agendada", "ao_vivo"] } },
  { "campo": "duracao_segundos", "op": "maior_igual", "valor": 300 } ] }
```

## Comportamento seguro

Não há regra anterior pra cair. Regra ausente ou inválida (JSON malformado, campo/operador
desconhecido, valor incompatível) significa **nenhum vídeo do canal relevante naquela execução**, e a
ingestão registra um `Error` com o motivo (alerta no Sentry). A ingestão dos outros canais continua.
Falhar parado custa só esperar a correção; processar vídeo errado custa tokens de IA.

A migration `20261027120000` converte as regras que existiam (enum) a partir dos valores da própria
linha, e a `20261027130000` torna a coluna obrigatória.

## Relevância gravada em cada vídeo (#184)

No processamento diário, para cada vídeo das páginas da playlist que a ingestão já percorre, ela grava em `video`
os dados que o YouTube devolveu (título, descrição, privacidade, duração, data de envio) e o veredito da regra em
`video.relevante`, seja relevante ou não, com o mesmo `UpsertAsync` do pipeline. Depois processa só os relevantes.
Não há varredura extra: a playlist inteira é coberta aos poucos, no mesmo caminhar por páginas (com o
`last_page_token` salvo) que o canal já faz.

- Cada vídeo é atualizado quando a execução passa por ele. Sem fila de processamento, a playlist inteira é percorrida
  numa execução; com fila grande, o ciclo pode levar alguns dias.
- `relevante` é recalculado a cada passagem com os dados do momento: título ou privacidade que mudam, ou uma regra
  editada, mudam a relevância na próxima vez que o vídeo for visitado.
- Para atualizar tudo agora, por script: zerar o ponto salvo da playlist (`processamento_progresso.last_page_token`
  do canal) e rodar a ingestão; ela percorre do topo ao fim.
- Colunas: `relevante`, `status` (`active` | `removed`), `created_at` (inserido em) e `updated_at` (última alteração
  da linha, por gatilho).
- O upsert do vídeo vindo do YouTube não leva `processed_at` nem dados do pipeline, e o PostgREST só altera as colunas
  presentes no corpo: transcrição, resumo, categorias e chunks nunca são tocados. Um vídeo que deixa de ser relevante
  depois de processado mantém seus dados, só passa a `relevante = false`.
- Quando o YouTube devolve `Deleted video` no lugar do vídeo, grava-se só `status = removed` (o título e o resto ficam
  como estavam) e o vídeo não é processado. `Private video` não traz dados reais: não é gravado nem processado.
- Se a gravação falhar, o canal processa os relevantes normalmente e o erro vai para o Sentry.
- `video.relevante` é `NULL` até o vídeo ser visitado pela primeira vez depois do deploy. Policy, views, funções
  `match_*` e painel ainda **não** leem a coluna; isso é a etapa seguinte (#184, PR B2), depois de conferir os dados.

## Ordem de deploy

Aplicar as migrations **antes** do código novo: com o código novo e sem a coluna, a ingestão não
processa vídeo nenhum até as migrations entrarem.

## Editar

Por script fora do repositório, com `UPDATE canal_ingestao SET regra_relevancia = '…'::jsonb WHERE
channel_id = '…'`. Teste a regra antes (`RegraRelevancia.Parse`) para não cair no alerta de regra
inválida. A regra vale na próxima execução da ingestão.