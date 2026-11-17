# Saga coreografada — teste local

Exchange topic durável `techchallenge.events`; fila durável própria `billing-service.events`.

| Entrada | Efeito |
| --- | --- |
| `os.criada` | Registra a OS pelo `ordemServicoUniqueCode`, sem apagar orçamento existente. |
| `execution.falhou` | Persiste a solicitação de compensação; worker consulta o pagamento e solicita refund integral ou cancelamento de Pix ainda pendente. |

| Saída | Condição |
| --- | --- |
| `billing.aprovado` | Pagamento correspondente à OS/valor confirmado como `processed/accredited` pelo provedor da cobrança. |
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

## Demonstração com pagamento Fake

O Compose inclui a API no perfil local `billing`. Para subir Billing e RabbitMQ com o Fake, execute na raiz deste repositório:

```powershell
$env:Payment__Provider = 'Fake'
docker compose --profile billing up -d --build --wait
```

O Swagger do Billing fica em `http://localhost:5080/swagger`. Para rodar o Billing no host, use a mesma variável com `dotnet run --project src/Billing.Api --launch-profile http` e inicie somente o RabbitMQ com `docker compose up -d --wait rabbitmq`.

`Payment:Provider` é `MercadoPago` por padrão. `Fake` exige `Development` ou `Testing`, e a API recusa iniciar com essa configuração nos demais ambientes. O container grava a Saga e as cobranças simuladas em `/data/billing.db`, no volume `billing-data`. O SQLite do Billing que roda no host permanece no caminho configurado no host. Em Compose próprio, configure `ASPNETCORE_ENVIRONMENT=Development`, `Payment__Provider=Fake`, o broker compartilhado e um volume para `Saga__DatabasePath`.

A configuração escolhe o provedor na primeira tentativa de pagamento de cada OS. O provedor fica gravado com a chave de aprovação. Pagamentos anteriores a essa configuração são tratados como Mercado Pago, inclusive o da OS `09156e8e-e7cd-4858-ba39-6b27c0d1bafc`. Use uma **nova OS** para a demonstração. Operações de pagamentos Mercado Pago continuam usando as credenciais desse provedor, se houver registros dele no banco que precisem de reconciliação.

1. Suba o OS Service e o Execution Service com seus bancos de teste e o RabbitMQ compartilhado. Configure os dois com `RABBITMQ_ENABLED=true`, `RABBITMQ_USER=billing`, `RABBITMQ_PASSWORD=billing-local`, `RABBITMQ_VHOST=/` e `RABBITMQ_PORT=5672`. Use `RABBITMQ_HOST=localhost` se rodarem no host ou `rabbitmq` se os containers estiverem na mesma rede do Compose do Billing. Inicie os três consumidores antes de criar a OS.
2. No Swagger do OS Service, autorize com o JWT do ambiente e chame `POST /api/v1/ordens-servico` com cliente, veículo e item de serviço válidos. Com os stubs locais do OS Service, use `{"clienteId":1,"veiculoId":1,"itensServicos":[{"servicoId":1}]}`. Guarde o `uniqueCode` da resposta. O OS Service publica `os.criada`. No Swagger do Billing, consulte `GET /api/billing/ordens/{guid}` até retornar `200`.
3. No Swagger do Billing, chame `POST /api/billing/ordens/{guid}/orcamento` com `{"amount":50}`. Depois chame `POST /api/billing/ordens/{guid}/aprovar` com `{"payerEmail":"cliente@example.com"}` e um novo `X-Idempotency-Key`. A resposta traz um `id` no formato `ORD-FAKE-…`, `status: processed` e `statusDetail: accredited`. Repetir a aprovação com a mesma chave e o mesmo corpo retorna o mesmo pagamento. O Fake aprova imediatamente e os campos de QR Code retornam `null`.
4. O Billing publica `billing.aprovado`. Confirme `status: 4` (`EmExecucao`) no Swagger do OS Service, em `GET /api/v1/ordens-servico/por-codigo/{guid}`, e `status: Pendente` no Swagger do Execution, em `GET /api/v1/execucoes/os/{guid}`.
5. O Swagger atual do Execution não expõe uma rota para disparar `execution.falhou`. Para simular a falha no mesmo contrato, abra `http://localhost:15672` (`billing` / `billing-local`), entre em **Exchanges → techchallenge.events → Publish message**, use a routing key `execution.falhou` e publique o JSON abaixo. Preencha um novo `eventId`, o `uniqueCode` da **nova OS** e a hora UTC atual.

   ```json
   {
     "eventId": "<novo UUID>",
     "ordemServicoUniqueCode": "<uniqueCode da nova OS>",
     "occurredAt": "<data e hora UTC no formato ISO 8601, com Z>",
     "motivo": "Falha simulada na demonstração"
   }
   ```

6. No Swagger do Billing, consulte `GET /api/billing/ordens/{guid}` até ver `compensationRequested: true` e `compensationStatus: refunded`. Em `GET /api/payments/{id}`, confira `status: refunded` e `statusDetail: refunded` no mesmo pagamento. A Saga registra a confirmação financeira no Billing. O contrato atual não publica um evento de estorno concluído para o OS Service.
7. Publique a mesma mensagem de falha de novo, com o mesmo `eventId` e o mesmo corpo. As duas consultas continuam mostrando `refunded` com o mesmo ID de pagamento.

## Verificação e limites

```powershell
dotnet build
$env:BILLING_TEST_RABBITMQ = "true"
dotnet test
Remove-Item Env:BILLING_TEST_RABBITMQ
```

Com o Compose local saudável, os testes cobrem RabbitMQ real, aprovação/rejeição, compensação e DLQ com o Fake e com HTTP simulado para o Mercado Pago. Sem a variável, os testes de broker real são ignorados.

Esta implementação mínima opera em **uma réplica**, com um orçamento e uma tentativa de pagamento por OS. O valor é informado por operador/API, sem catálogo de preços ou autenticação novos. Persista o SQLite; não compartilhe o arquivo entre réplicas. Reembolso é integral; reembolsos parciais, aprovação de revisões e reconciliação de operações criadas fora deste fluxo exigem tratamento adicional. Compensação em estado inconclusivo permanece pendente para análise. Nenhuma infraestrutura de outro microsserviço foi alterada.

Referências dos endpoints implementados: [refund da Orders API](https://www.mercadopago.com.br/developers/pt/reference/online-payments/checkout-api/refund-order/post) e [cancelamento da Orders API](https://www.mercadopago.com.br/developers/pt/reference/online-payments/checkout-api/cancel-order/post). Um POST aceito não confirma sozinho o estorno: a implementação consulta a order e verifica estado, identificação e soma dos refunds processados.
