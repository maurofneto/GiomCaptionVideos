---
name: investigar-erro-cloudwatch-sentry
description: Investiga a causa raiz de um erro/crash em dev ou prod deste projeto, cruzando os logs do CloudWatch (ECS) com issues do Sentry. Use sempre que o usuário disser "deu erro", "quebrou", "analisa os logs", "investiga esse crash", "por que falhou", mencionar um log group/stream/task ID da ECS, colar uma stack trace, ou pedir pra descobrir a causa de uma falha — mesmo sem citar Sentry ou CloudWatch explicitamente.
---

# Investigar erro (CloudWatch + Sentry)

## Passos

1. **Ache o log certo.** Se o usuário já deu log group/stream, use direto. Se só deu um
   `taskId` ou disse "a execução de agora", ache o stream mais recente:
   ```bash
   MSYS_NO_PATHCONV=1 aws logs describe-log-streams \
     --log-group-name "/ecs/giom-instituto-dev" \
     --order-by LastEventTime --descending --max-items 3 --output json
   ```
   Sempre prefixe com `MSYS_NO_PATHCONV=1` — Git Bash no Windows reescreve o path que começa
   com `/` e quebra a chamada.

2. **Puxe os eventos e ache a causa raiz** — não a stack trace inteira:
   ```bash
   MSYS_NO_PATHCONV=1 aws logs get-log-events \
     --log-group-name "<grupo>" --log-stream-name "<stream>" --output json
   ```
   A causa real costuma estar na primeira linha `Unhandled exception. System.XxxException:
   <mensagem>` ou num `[ERR]` do Serilog — o resto do stack trace (`at Microsoft.Extensions...`,
   `at System.Collections...`) é ruído de framework, só vale a pena ler se a mensagem sozinha
   não deixar claro onde/por quê.

3. **Cruze com o Sentry** — mesmo erro pode já estar capturado lá com mais contexto (spans,
   sessão, breadcrumbs) do que o log cru mostra:
   - `search_issues`/`search_events` (ferramentas MCP do Sentry) filtrando por ambiente
     (`instituto-dev`, `ministerio-dev`, `instituto-prod`, `ministerio-prod`,
     `agenteinstituto-dev`, `agenteinstituto-prod` — reparar que o ambiente do bot tem prefixo
     diferente dos workers) e pela janela de tempo do log.
   - Se achar a issue correspondente, ela costuma ter o link direto — inclua na resposta.
   - Se **não** achar nada no Sentry pro mesmo horário/ambiente, isso também é informação:
     pode ser um erro que acontece antes do Sentry inicializar (ex: falha de DI na composição
     do host, que é exatamente esse tipo de caso — acontece na primeira linha de `Main`, antes
     do SDK do Sentry estar de pé).

4. **Distinga causa raiz de sintoma.** Um `NullReferenceException` em código de aplicação
   quase sempre é sintoma de uma pré-condição não satisfeita mais abaixo na pilha (config
   faltando, secret vazio, recurso externo não provisionado) — não pare no primeiro frame,
   suba a pilha até achar o "porquê" acionável.

5. **Reporte de forma acionável**: causa raiz em 1-2 frases, se é código ou configuração/infra,
   e o próximo passo concreto — não despeje o JSON/log inteiro na resposta pro usuário.

## Exemplo real (pra calibrar o nível de detalhe)

Um crash de `GoogleWebAuthorizationBroker.AuthorizeAsync` tentando abrir `xdg-open` num
container ECS headless não é "bug de código" — é sintoma de um secret nunca provisionado
(`RefreshToken` vazio no SSM), que faz o fluxo cair no caminho interativo (que só funciona
numa máquina local com navegador). A causa raiz reportada foi essa, não a exceção de
`Process.Start` em si.
