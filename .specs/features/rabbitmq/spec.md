# RabbitMQ para eventos de pagamento

## Escopo e decisões

Publicar o estado atual consultado no Mercado Pago depois de validar um webhook de Order. A API continua responsável por criar e consultar Pix; outros serviços podem consumir os eventos. Não há consumidor de negócio nem banco/outbox neste repositório.

- RMQ-01: webhook assinado de Order deve publicar `payment.status.updated.v1`, contendo ID estável do estado, data de observação, IDs da order e do pedido, valor, status e detalhe. Não incluir token, dados do pagador ou QR Code.
- RMQ-02: declarar exchange topic e fila duráveis, com binding; publicar JSON persistente, obrigatório e com confirmação do broker antes do HTTP 200.
- RMQ-03: falha ou timeout de publicação deve retornar ProblemDetails 503 e permitir nova tentativa. Reconectar na próxima publicação após uma falha. Assinaturas inválidas, outros tópicos e falhas do Mercado Pago não publicam eventos.
- RMQ-04: reentregas podem produzir mensagens duplicadas, com o mesmo `eventId` para a mesma combinação order/status/detalhe; consumidores devem deduplicar de forma persistente. Não prometer entrega exatamente uma vez nem ordem de transições.
- RMQ-05: oferecer Docker Compose local com volume e painel de administração, configuração por seção RabbitMq/User Secrets/ambiente, testes sem serviços externos e teste opt-in com broker real.

## Execução (três entregas)

1. Publicador e infraestrutura: `src/Billing.Api/Messaging/*`, projeto da API, `Program.cs`, `appsettings.json`, `compose.yaml` e testes do publicador. Verificar build, falha real de conexão e contrato com broker quando disponível. Commit `feat(messaging): add confirmed rabbitmq payment publisher`.
2. Webhook: controller, exception handler, factory e testes HTTP. Verificar publicação somente após validação/consulta, reentrega, falha/retentativa e ausência de publicação nos demais fluxos. Commit `feat(webhooks): publish payment state to rabbitmq`.
3. Documentação: README e memória em `.specs`. Verificar instruções, Compose, build e testes completos. Commit `docs(messaging): document rabbitmq setup and delivery semantics`.

## Verificação

- RMQ-01 a RMQ-05 implementados e verificados: 38 testes aprovados, incluindo dois com RabbitMQ 4.2.9 real, sem cobranças externas.
- Publisher: JSON persistente, IDs estáveis, confirmações e mandatory; testes de timeout com TCP sem AMQP, cancelamento, mensagem sem rota, recuperação e publicação concorrente.
- Webhook: teste completo até fila com provedor simulado; testes HTTP para validação, consulta do estado atual, duplicatas, confirmação antes de 200 e erro 503 seguido de reentrega bem-sucedida.
- Compose validado e broker iniciado saudável. README documenta execução, configuração, painel, contrato, consumo/deduplicação e limites sem outbox.
- Build final sem avisos/erros. API reiniciada; Health, Swagger e documento OpenAPI responderam 200, com 503 documentado para falha de publicação no webhook.
