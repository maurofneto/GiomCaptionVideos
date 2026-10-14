# Runbook — Grok Voice (xAI speech-to-text) na cadeia de Transcrição

US #148. Elo `GrokVoice` da cadeia `etapa_provedor_config` (etapa `transcricao`), **antes do Whisper**.
Transcreve o áudio inteiro numa requisição (`POST https://api.x.ai/v1/stt`, modelo
`grok-voice-transcribe-2.0`, `language=pt`) — sem o limite de 25 MB da API da OpenAI.

## O que precisa existir

1. **Chave da API** no SSM, `SecureString`: `/giom/{env}/pipeline/GrokVoice/ApiKey`
   (o worker carrega o prefixo `pipeline/`; vira a config `GrokVoice:ApiKey`). Quem coloca o valor é o Mauro.
2. **Migration** `20261014120000_etapa_provedor_config_permite_grokvoice.sql` aplicada no banco do ambiente
   (amplia o CHECK de `provedor`; não muda comportamento sozinha).
3. **Linha na cadeia** — só quando for ligar o elo (primeiro em **dev**):

```sql
-- Whisper vira 5º elo; GrokVoice entra como 4º (antes do Whisper), pros dois canais.
UPDATE etapa_provedor_config SET ordem = 5
 WHERE etapa = 'transcricao' AND provedor = 'Whisper';

INSERT INTO etapa_provedor_config (channel_id, etapa, provedor, ordem, prompt, usar_observabilidade) VALUES
 ('UCyyXFx2xHdzfavx6H6Q8zJw', 'transcricao', 'GrokVoice', 4, NULL, false),  -- Instituto
 ('UCyHMyZH5oon8jW7y_yMRstQ', 'transcricao', 'GrokVoice', 4, NULL, false)   -- Ministério
ON CONFLICT (channel_id, etapa, provedor) DO NOTHING;
```

> A ordem real em dev pode diferir do seed da migration `20260812023700` (ex.: `YtDlpCaption` em 1º).
> Confira antes: `select channel_id, ordem, provedor from etapa_provedor_config where etapa='transcricao' order by 1,2;`
> e ajuste os números pra GrokVoice ficar logo antes do Whisper.

**Desligar** o elo: `DELETE FROM etapa_provedor_config WHERE etapa='transcricao' AND provedor='GrokVoice';`
(e, se quiser, devolver o Whisper ao `ordem` anterior).

## Limite conhecido

O áudio vem de `IAudioDownloader` (yt-dlp). Rodando na AWS, o YouTube bloqueia o download por IP de
datacenter sem **cookies válidos** (Bug #144; ver `runbook-ytdlp-cookies.md`). Trocar o provedor de
transcrição **não** remove essa dependência: com cookies inválidos a cadeia falha antes de chegar ao Grok.

## Custo

Tabela da xAI: US$ 0,10 por hora de áudio (REST) — uma aula de 1h45 custa ~US$ 0,18. A estimativa vai
na telemetria (`provedor=xAI`). Conferir `docs.x.ai/developers/models/speech-to-text` antes de confiar.
