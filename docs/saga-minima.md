# Saga coreografada — teste local

Exchange topic durável `techchallenge.events`; fila durável própria `billing-service.events`.

| Entrada | Efeito |
| --- | --- |
| `os.criada` | Registra a OS pelo `ordemServicoUniqueCode`, sem apagar orçamento existente. |
| `execution.falhou` | Persiste a solicitação de compensação; worker consulta o pagamento e solicita refund integral ou cancelamento de Pix ainda pendente. |

| Saída | Condição |
| --- | --- |
| `billing.aprovado` | Pagamento correspondente à OS/valor confirmado como `processed/accredited` pelo Mercado Pago. |
| `billing.reprovado` | Rejeição explícita do orçamento antes de iniciar pagamento. Falhas técnicas não geram esse evento. |

Todos usam JSON camelCase com `eventId` Guid, `ordemServicoUniqueCode` Guid e `occurredAt` UTC. O evento financeiro legado `payment.status.updated.v1` em `billing.events` continua funcionando.

## Configuração

O ASP.NET Core lê variáveis de ambiente, incluindo `RabbitMq__HostName`, `RabbitMq__Port`, `RabbitMq__VirtualHost`, `RabbitMq__UserName`, `RabbitMq__Password`, `MercadoPago__AccessToken` e `MercadoPago__WebhookSecret`. Use credenciais de teste configuradas localmente, nunca na conversa/Git. `Saga__Enabled` habilita os workers (padrão `true`); `Saga__DatabasePath` aponta para SQLite (padrão `.data/billing.db` dentro do content root da API). Em container, monte esse arquivo/diretório em volume persistente. `Saga__PollSeconds` e `Saga__MaxFailures` controlam polling e limite de falhas (5 s e 10).

```powershell
docker compose up -d --wait
dotnet run --project src/Billing.Api --launch-profile http
```

## Teste entre serviços

1. Inicie os consumidores dos outros serviços com filas próprias vinculadas a `billing.aprovado` e `billing.reprovado`. Para inspeção local, crie no painel RabbitMQ uma fila de teste e faça binding `billing.#` na exchange `techchallenge.events`.
2. Publique `os.criada` nessa exchange, pelo serviço de OS ou pelo painel RabbitMQ, com body:

```json
{"eventId":"10256e11-cffd-4846-8049-85252ca6381f","ordemServicoUniqueCode":"6278720e-81c5-465f-8078-f2f623f3a945","occurredAt":"2026-11-17T12:00:00Z"}
```

3. Use as requisições em [`Billing.Saga.http`](../src/Billing.Api/Billing.Saga.http): gerar orçamento com `amount`, aprovar com dados do pagador e `X-Idempotency-Key`, consultar o contexto. Aprovar retorna os dados Pix do fluxo atual; o evento distribuído aguarda pagamento confirmado. A OS é enviada como `external_reference` e o valor vem do orçamento local. Timeout preserva payload/chave para retomada.
4. Receba um webhook assinado real de teste. O worker também consulta pagamentos pendentes, recuperando confirmação caso a notificação não chegue. Confira `billing.aprovado`. Para testar `billing.reprovado`, use outra OS e rejeite o orçamento antes de aprovar/criar Pix.
5. Publique `execution.falhou` com novo `eventId` e o mesmo `ordemServicoUniqueCode`. Consulte a OS: `compensationStatus` ficará `pending` até confirmação do provedor; depois `refunded`, `cancelled` ou `not_applicable` se não havia pagamento. Não há evento de sucesso de estorno neste contrato.
6. Publique um JSON inválido em `os.criada`: a mensagem será enviada a `billing-service.events.error` via DLQ. Após corrigir a causa, publique novamente o evento na routing key original. Duplicatas já processadas são reconhecidas pela inbox; reutilizar `eventId` com outra OS/conteúdo é erro.

O ACK ocorre após persistir contexto + inbox; para compensação, isso inclui o trabalho durável que será executado pelo worker. Estado e eventos de saída são gravados na mesma transação. A publicação aguarda confirmação do broker e exige rota; sem assinante, a saída fica pendente e é retentada sem perder o `eventId`. Confirmar publicação e cair antes de marcar como enviada ainda pode produzir duplicatas: os destinatários também precisam deduplicar.

Falhas HTTP de criação/compensação são retentadas com a mesma chave, com espera exponencial até 5 min. Após o limite, a reconciliação é suspensa em `manual_review` e `lastError` informa a categoria. Corrija a causa e use `POST /api/billing/ordens/{guid}/reconciliar` para retomar sem gerar outra operação. Estados financeiros ainda em processamento permanecem pendentes e não são considerados sucesso.

## Verificação e limites

```powershell
dotnet build
$env:BILLING_TEST_RABBITMQ = "true"
dotnet test
Remove-Item Env:BILLING_TEST_RABBITMQ
```

Com o Compose local saudável, os testes cobrem RabbitMQ real, aprovação/rejeição, compensação e DLQ usando Mercado Pago simulado: não criam cobranças nem estornos reais. Sem a variável, testes de broker real ficam explicitamente ignorados.

Esta implementação mínima opera em **uma réplica**, com um orçamento e uma tentativa de pagamento por OS. O valor é informado por operador/API, sem catálogo de preços ou autenticação novos. Persista o SQLite; não compartilhe o arquivo entre réplicas. Reembolso é integral; reembolsos parciais, aprovação de revisões e reconciliação de operações criadas fora deste fluxo exigem tratamento adicional. Compensação em estado inconclusivo permanece pendente para análise. Nenhuma infraestrutura de outro microsserviço foi alterada.

Referências dos endpoints implementados: [refund da Orders API](https://www.mercadopago.com.br/developers/pt/reference/online-payments/checkout-api/refund-order/post) e [cancelamento da Orders API](https://www.mercadopago.com.br/developers/pt/reference/online-payments/checkout-api/cancel-order/post). Um POST aceito não confirma sozinho o estorno: a implementação consulta a order e verifica estado, identificação e soma dos refunds processados.
