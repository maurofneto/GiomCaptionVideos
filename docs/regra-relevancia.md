# Regra de relevância por canal

Define quais vídeos de um canal entram no pipeline. Fica em `canal_ingestao.regra_relevancia`
(JSON), é lida pelo `Ingestao.Worker` a cada execução e muda por `UPDATE`, sem deploy (Config First).

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

- Texto não diferencia maiúsculas de minúsculas.
- Duração ou data de envio desconhecidas nunca casam (nem em `menor`/`antes`).
- Datas comparam por dia, ignorando a hora.
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

- Coluna `NULL`: o canal segue pela regra antiga (`relevancia_tipo` + `relevancia_valores` +
  `privacy_status_filtro`), convertida para a mesma árvore.
- Regra inválida (JSON malformado, campo/operador desconhecido, valor incompatível): a ingestão do
  canal **não para**; usa a regra antiga e registra um `Error` (alerta no Sentry) com o motivo.
- A migration converte as regras atuais para o formato novo a partir dos valores da própria linha e
  não sobrescreve uma regra já editada.

## Editar

Por script fora do repositório, com `UPDATE canal_ingestao SET regra_relevancia = '…'::jsonb WHERE
channel_id = '…'`. Teste a regra antes (`RegraRelevancia.Parse`) para não cair no alerta de regra
inválida.
