# Como o OS Service chama o Billing

**URL local do Billing** `http://localhost:5080` · **Swagger** `/swagger` · **Autenticação** sem token no Billing

O dev do OS Service precisa configurar o RabbitMQ compartilhado e adicionar as chamadas HTTP de orçamento e Pix. O OS Service já publica e recebe os eventos abaixo.

## 1. Conecte o RabbitMQ

Use o mesmo broker e virtual host nos dois serviços. A exchange é `techchallenge.events`. Ao criar uma OS, o OS Service publica `os.criada` e o Billing registra a ordem.

Se as duas APIs rodam na máquina e o RabbitMQ é do Compose do Billing, use estas variáveis no OS Service.

```ini
RABBITMQ_HOST=localhost
RABBITMQ_PORT=5672
RABBITMQ_USER=billing
RABBITMQ_PASSWORD=billing-local
RABBITMQ_VHOST=/
RABBITMQ_ENABLED=true
```

## 2. Gere o orçamento e o Pix

Substitua `{guid}` pelo **`uniqueCode` retornado ao criar a OS**. Se o orçamento retornar `404`, aguarde o consumo de `os.criada` e tente de novo. Se persistir, confira a conexão com o RabbitMQ.

**Gerar orçamento**

```http
POST /api/billing/ordens/{guid}/orcamento
Content-Type: application/json

{"amount":50.00}
```

**Cliente aceitou? Gere o Pix**

```http
POST /api/billing/ordens/{guid}/aprovar
Content-Type: application/json
X-Idempotency-Key: <UUID gerado para esta aprovação>

{"payerEmail":"cliente@example.com"}
```

O `200` retorna o `id` (`ORD...`) e os campos do Pix, como `qrCode` (Pix Copia e Cola). Use `GET /api/payments/{id}` para consultar a cobrança se o QR Code ainda estiver pendente. Em caso de timeout, repita a aprovação com **a mesma chave e o mesmo corpo**.

Se o cliente recusar antes de iniciar o Pix, envie `POST /api/billing/ordens/{guid}/reprovar` sem corpo.

## 3. Aguarde a confirmação

O Billing publica **`billing.aprovado` após confirmar o pagamento**. Ao receber esse evento, o OS Service avança a OS para `EmExecucao`. O `200` de `/aprovar` apenas indica que o Billing retornou a cobrança. A reprovação explícita publica `billing.reprovado` e deixa a OS em `AguardandoAprovacao`.

Para consultar a decisão no Billing, use `GET /api/billing/ordens/{guid}`.
