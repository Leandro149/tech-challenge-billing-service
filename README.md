# Tech Challenge — Billing Service

API em C# / ASP.NET Core 10 para integrar o frontend de um trabalho acadêmico ao Pix do Mercado Pago, usando a Orders API. Este repositório contém apenas o backend.

## Saga e fluxo distribuído — Épico 2

A **TC4-08 — Definir estratégia do Saga Pattern** está documentada no [ADR-0001](docs/architecture/adr-0001-saga-os.md). A decisão adota uma Saga orquestrada no serviço de OS e define estados, contratos, compensações, correlação com billing, persistência e recuperação. [Requisitos e situação das etapas](.specs/features/saga/spec.md).

Esta entrega define a arquitetura. O orquestrador e as etapas TC4-09 (criação da OS), TC4-10 (orçamento) e TC4-11 (aprovação) ainda precisam ser implementados nos serviços responsáveis. A API de billing mantém o contrato atual de pagamentos e eventos RabbitMQ.

## Executar

Pré-requisitos: SDK do .NET 10; Docker com suporte a containers Linux e Docker Compose para executar o RabbitMQ local.

Na raiz do repositório, configure as credenciais locais de uma **aplicação/conta de teste**:

```powershell
docker compose up -d --wait
.\scripts\Set-LocalSecrets.ps1
dotnet run --project src/Billing.Api --launch-profile http
```

O script pede o Access Token com entrada oculta e salva em .NET User Secrets, fora do repositório. A assinatura do webhook é opcional até você configurar as notificações. User Secrets é uma configuração local de desenvolvimento, não um cofre criptografado.

- API: http://localhost:5080
- Swagger: http://localhost:5080/swagger
- Health: http://localhost:5080/health (verifica a API, não a credencial do Mercado Pago nem a conexão com RabbitMQ)
- Painel RabbitMQ: http://localhost:15672 (usuário `billing`, senha local de demonstração `billing-local`)

Alternativamente, configure User Secrets diretamente (substitua os placeholders localmente):

```powershell
dotnet user-secrets set "MercadoPago:AccessToken" "SEU_ACCESS_TOKEN_DE_TESTE" --project src/Billing.Api
dotnet user-secrets set "MercadoPago:WebhookSecret" "SUA_ASSINATURA_SECRETA" --project src/Billing.Api
```

Também são aceitas as variáveis `MercadoPago__AccessToken` e `MercadoPago__WebhookSecret`. Arquivos `.env` não são carregados automaticamente. Não coloque tokens no `appsettings.json`, frontend ou Git. A Public Key não é necessária para este fluxo de criação de Pix pelo backend.

**Ambiente de teste:** a Orders API usa a mesma URL `https://api.mercadopago.com` nos testes; o ambiente depende da aplicação/conta e da credencial configurada. O prefixo `APP_USR` sozinho não distingue teste de produção. Não use um token da conta recebedora real para esta demonstração. Nenhuma credencial compartilhada na conversa foi incorporada ao código ou à configuração local.

## Contrato com o frontend

| Método e rota | Função |
| --- | --- |
| `POST /api/payments/pix` | Cria uma order com pagamento Pix |
| `GET /api/payments/{orderId}` | Consulta a order atual no Mercado Pago |
| `POST /api/webhooks/mercado-pago` | Recebe notificações assinadas de Orders |

Para criar uma cobrança, envie o header `X-Idempotency-Key` com um UUID. Guarde e reutilize a mesma chave e os mesmos dados se houver timeout ou se você repetir a operação. Uma nova chave representa uma nova tentativa de cobrança; não gere outra automaticamente após uma resposta incerta.

```http
POST /api/payments/pix
Content-Type: application/json
X-Idempotency-Key: 5c99517b-47ee-4654-8c1e-8bd9c5151250

{
  "orderId": "pedido-123",
  "amount": 50.00,
  "payerEmail": "test_payer@example.com",
  "payerFirstName": "APRO"
}
```

`orderId` é o ID do pedido no seu sistema, com até 64 letras, números, hífens ou sublinhados. O valor deve ser positivo, ter até duas casas decimais e não ultrapassar `999999999.99`. `payerFirstName` é opcional. No cenário oficial de teste Pix, o nome `APRO` provoca a aprovação automática posterior; não deve ser fixado como nome de um comprador real.

Resposta `201 Created` (valores ilustrativos):

```json
{
  "id": "ORD01HRYFWNYRE1MR1E60MW3X0T2P",
  "orderId": "pedido-123",
  "amount": 50.00,
  "status": "action_required",
  "statusDetail": "waiting_transfer",
  "qrCode": "PIX_COPIA_E_COLA_RETORNADO_PELO_PROVEDOR",
  "qrCodeBase64": "IMAGEM_BASE64_RETORNADA_PELO_PROVEDOR",
  "ticketUrl": "https://www.mercadopago.com.br/..."
}
```

`id` é o identificador `ORD...` do Mercado Pago. Use esse valor no GET, e não o ID do pedido do seu sistema. O GET devolve o mesmo contrato, com o estado atualizado. A criação pode ser assíncrona: os campos Pix podem estar `null` enquanto a order está sendo processada. Nesse caso, consulte novamente após alguns segundos.

No frontend, mostre `qrCode` como Pix Copia e Cola, `qrCodeBase64` em uma imagem e/ou `ticketUrl` como link. A geração do QR Code não confirma pagamento. Para este fluxo Pix, `processed` com `statusDetail: accredited` representa a confirmação; `action_required`/`waiting_transfer` representa aguardar pagamento. Preserve e trate os demais estados retornados pelo provedor, inclusive cancelamento e reembolso.

Exemplo JavaScript independente de framework:

```javascript
const api = "http://localhost:5080";

// Crie e guarde esta chave ao iniciar a operação, antes da primeira chamada.
// Reutilize a mesma chave e payload ao repetir essa operação.
const idempotencyKey = crypto.randomUUID();
const payload = {
  orderId: "pedido-123",
  amount: 50.00,
  payerEmail: "test_payer@example.com",
  payerFirstName: "APRO" // apenas para a simulação oficial de teste
};

async function createPix() {
  const response = await fetch(`${api}/api/payments/pix`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-Idempotency-Key": idempotencyKey
    },
    body: JSON.stringify(payload)
  });
  const body = await response.json();
  if (!response.ok) throw new Error(body.detail ?? body.title ?? "Falha ao criar Pix");
  return body;
}

async function getPayment(mercadoPagoOrderId) {
  const response = await fetch(`${api}/api/payments/${encodeURIComponent(mercadoPagoOrderId)}`);
  const body = await response.json();
  if (!response.ok) throw new Error(body.detail ?? body.title ?? "Falha ao consultar Pix");
  return body;
}

// const payment = await createPix();
// Copia e cola: payment.qrCode
// Imagem PNG: `data:image/png;base64,${payment.qrCodeBase64}`
// Consulta posterior: await getPayment(payment.id)
```

As origens `http://localhost:3000`, `http://localhost:4200` e `http://localhost:5173` estão liberadas no CORS. Edite `Cors:AllowedOrigins` em `src/Billing.Api/appsettings.json` se seu frontend usar outra origem, incluindo protocolo e porta. CORS não autentica o usuário.

## Webhook

1. Exponha a API por uma URL HTTPS pública, por exemplo usando um túnel durante o desenvolvimento. O Mercado Pago não acessa `localhost` diretamente.
2. No painel da aplicação, configure Webhooks para o ambiente de teste, habilitando o tópico **Orders**.
3. Cadastre `https://SEU_HOST/api/webhooks/mercado-pago`.
4. Copie a assinatura secreta para `MercadoPago:WebhookSecret` nos User Secrets e reinicie a API.

O Mercado Pago enviará `data.id` e `type=order` na query string, com os headers `x-signature` e `x-request-id`. A API valida HMAC-SHA256 em tempo constante, usa o ID assinado da query para consultar a order, publica o estado atual no RabbitMQ e registra o status no log. Timestamps em segundos e milissegundos são aceitos, com tolerância de cinco minutos; mantenha o relógio do servidor sincronizado. O ID mantém a capitalização recebida, seguindo o SDK .NET oficial.

Notificações sem assinatura válida retornam `401`. Sem segredo configurado, o endpoint retorna `503`. Falhas na consulta ao Mercado Pago ou na publicação no RabbitMQ retornam erro para permitir nova entrega. O `200` com `{"received":true}` só é retornado após a confirmação do broker. Reentregas são aceitas e podem publicar eventos duplicados; consumidores precisam deduplicar pelo `eventId`. Não há confirmação de pedido nem banco neste projeto.

O frontend consulta o GET para atualizar a tela. Ele não recebe o webhook diretamente.

## RabbitMQ

O fluxo implementado é: webhook assinado → consulta à Orders API → publicação do estado atual → confirmação do broker → HTTP `200`. POST de criação de Pix e GET de consulta não publicam eventos. Um consumidor em outro serviço pode usar o evento para atualizar um pedido; este repositório implementa o publicador.

Inicie o broker e depois a API:

```powershell
docker compose up -d --wait
dotnet run --project src/Billing.Api --launch-profile http
```

O Compose usa a [imagem oficial do RabbitMQ](https://hub.docker.com/_/rabbitmq), volume persistente e portas publicadas apenas em `127.0.0.1`. As credenciais `billing` / `billing-local` são exemplos para desenvolvimento local. Para outro broker, configure `RabbitMq:HostName`, `RabbitMq:Port`, `RabbitMq:VirtualHost`, `RabbitMq:UserName` e `RabbitMq:Password` via User Secrets ou variáveis de ambiente, como `RabbitMq__Password`; não versione credenciais reais. Esta configuração local usa AMQP sem TLS.

| Recurso | Valor padrão |
| --- | --- |
| AMQP | `localhost:5672` |
| Painel | http://localhost:15672 |
| Exchange topic durável | `billing.events` |
| Routing key e tipo AMQP | `payment.status.updated.v1` |
| Fila durável | `billing.payment-status` |
| Timeout da publicação | `RabbitMq:PublishTimeoutSeconds` = `5` |

Exchange e fila são configuráveis por `RabbitMq:Exchange` e `RabbitMq:Queue`. A conexão é aberta e os recursos são declarados no primeiro webhook válido que consiga consultar a order. Portanto, a fila pode ainda não aparecer no painel logo após iniciar a API. A API pode iniciar sem o broker, mas um webhook de Order não será aceito com sucesso enquanto a publicação falhar. O cliente pode levar alguns segundos adicionais para encerrar uma conexão que deixou de responder após o timeout.

Cada publicação contém JSON em camelCase, com as propriedades AMQP `ContentType=application/json`, mensagem persistente, `Type=payment.status.updated.v1` e `MessageId` igual ao `eventId`:

```json
{
  "eventId": "<sha256-da-combinacao-tipo-paymentId-status-statusDetail>",
  "observedAt": "2026-11-16T15:00:00+00:00",
  "paymentId": "ORD123",
  "orderId": "pedido-123",
  "amount": 50.00,
  "status": "processed",
  "statusDetail": "accredited"
}
```

`paymentId` identifica a order no Mercado Pago; `orderId` é a referência do pedido no seu sistema e pode ser `null`. O evento informa o estado consultado, inclusive estados de espera, cancelamento ou reembolso. `observedAt` é o momento da observação pela API, não a data da transição no provedor. Dados do pagador, token e conteúdo Pix não são publicados.

As mensagens usam [publisher confirms](https://www.rabbitmq.com/tutorials/tutorial-seven-dotnet) e `mandatory=true`: rejeição pelo broker, mensagem sem rota ou timeout resultam em `503`. A próxima tentativa recria conexão/canal e declara novamente a topologia após uma falha. Não existe outbox ou fila de retentativas em memória: a recuperação depende da reentrega do webhook pelo Mercado Pago.

O mesmo `paymentId`/`status`/`statusDetail` gera o mesmo `eventId`, mesmo em entregas diferentes. O consumidor deve persistir a deduplicação junto com os efeitos de negócio e só enviar o ACK após concluir o processamento. Duplicatas são possíveis, inclusive se o broker aceitou a mensagem mas a confirmação se perdeu. Não há garantia de entrega exatamente uma vez, ordenação das transições ou histórico completo; notificações concorrentes podem observar estados diferentes. Para evitar regressão de status no serviço consumidor, reconcilie estados conflitantes com a fonte de verdade.

Para conferir manualmente, crie um Pix de teste pelo Swagger, usando credencial de teste, e receba ou simule no Mercado Pago uma notificação com o ID `ORD...` existente. Depois, no painel RabbitMQ, abra **Queues and Streams → billing.payment-status → Get messages**, usando requeue para manter a mensagem disponível. Consultar a URL do webhook no navegador envia GET e não testa esse fluxo POST assinado.

Para encerrar o broker preservando as mensagens no volume:

```powershell
docker compose down
```

## Erros e limites da demonstração

Os erros usam ProblemDetails, sem expor o token ou o corpo de erro do provedor:

| HTTP | Significado |
| --- | --- |
| `400` | Dados de entrada ou chave de idempotência inválidos |
| `401` | Assinatura do webhook inválida |
| `404` | Order não encontrada |
| `409` | Conflito na operação |
| `422` | Dados recusados pelo Mercado Pago |
| `502` | Falha de comunicação, credencial recusada ou resposta inválida do provedor |
| `503` | Configuração ausente, limite de chamadas atingido ou publicação no RabbitMQ não confirmada |
| `504` | Timeout no Mercado Pago |

Esta API é uma demonstração sem autenticação e sem persistência local. Aceita o valor informado na requisição porque não existe um serviço de pedidos/catálogo neste repositório. Para uso real, o backend deve obter e validar o preço do pedido, autorizar o acesso de cada usuário à cobrança e persistir os efeitos de negócio de forma idempotente. A Public Key e o Access Token não substituem a autenticação dos usuários da sua API.

## Verificar

```powershell
dotnet build
dotnet test
```

Por padrão, os testes usam um servidor HTTP de teste, um provedor e um publicador simulados. Também verificam o timeout do cliente real contra um servidor TCP que não responde ao AMQP. Cobrem contratos de criação/consulta, validação de entrada, idempotência enviada ao provedor, erros, CORS, assinatura/reentrega de webhook, conteúdo do evento e confirmação da publicação antes da resposta HTTP; não criam cobranças externas. O arquivo `src/Billing.Api/Billing.Api.http` contém requisições para execução manual com uma credencial de teste configurada.

Dois testes com RabbitMQ real ficam explicitamente ignorados até habilitá-los. Com o Compose saudável e as credenciais locais padrão, execute no PowerShell:

```powershell
$env:BILLING_TEST_RABBITMQ = "true"
try {
    dotnet test
} finally {
    Remove-Item Env:BILLING_TEST_RABBITMQ -ErrorAction SilentlyContinue
}
```

Esses testes comprovam a entrega de webhook assinado até a fila, propriedades de persistência, payload, publicação concorrente, falha por falta de binding e recuperação. Usam recursos temporários exclusivos e removem apenas esses recursos; não consomem mensagens da fila de negócio. O provedor de pagamento permanece simulado.

## Documentação oficial

- [Criar order](https://www.mercadopago.com.br/developers/pt/reference/online-payments/checkout-api/create-order/post)
- [Integração Pix](https://www.mercadopago.com.br/developers/pt/docs/checkout-api-orders/payment-integration/websites/pix)
- [Teste Pix com APRO](https://www.mercadopago.com.br/developers/pt/docs/checkout-api-orders/integration-test/pix)
- [Notificações de Orders](https://www.mercadopago.com.br/developers/pt/docs/checkout-api-orders/notifications)
- [Validador de webhook do SDK .NET](https://github.com/mercadopago/sdk-dotnet/blob/master/src/MercadoPago/Webhook/WebhookSignatureValidator.cs)
