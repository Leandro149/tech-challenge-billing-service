# Tech Challenge — Billing Service

API em C# / ASP.NET Core 10 para integrar o frontend de um trabalho acadêmico ao Pix do Mercado Pago, usando a Orders API. Este repositório contém apenas o backend.

## Executar

Pré-requisito: SDK do .NET 10.

Na raiz do repositório, configure as credenciais locais de uma **aplicação/conta de teste**:

```powershell
.\scripts\Set-LocalSecrets.ps1
dotnet run --project src/Billing.Api --launch-profile http
```

O script pede o Access Token com entrada oculta e salva em .NET User Secrets, fora do repositório. A assinatura do webhook é opcional até você configurar as notificações. User Secrets é uma configuração local de desenvolvimento, não um cofre criptografado.

- API: http://localhost:5080
- Swagger: http://localhost:5080/swagger
- Health: http://localhost:5080/health (verifica a API, não a credencial do Mercado Pago)

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

O Mercado Pago enviará `data.id` e `type=order` na query string, com os headers `x-signature` e `x-request-id`. A API valida HMAC-SHA256 em tempo constante, usa o ID assinado da query para consultar a order e registra o status atual no log. Timestamps em segundos e milissegundos são aceitos, com tolerância de cinco minutos; mantenha o relógio do servidor sincronizado. O ID mantém a capitalização recebida, seguindo o SDK .NET oficial.

Notificações sem assinatura válida retornam `401`. Sem segredo configurado, o endpoint retorna `503`. Falhas na consulta ao Mercado Pago retornam erro para permitir nova entrega. Reentregas são aceitas e apenas repetem a consulta; não há confirmação de pedido, banco ou publicação de eventos neste projeto.

O frontend consulta o GET para atualizar a tela. Ele não recebe o webhook diretamente.

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
| `503` | Configuração ausente ou limite de chamadas atingido |
| `504` | Timeout no Mercado Pago |

Esta API é uma demonstração sem autenticação e sem persistência local. Aceita o valor informado na requisição porque não existe um serviço de pedidos/catálogo neste repositório. Para uso real, o backend deve obter e validar o preço do pedido, autorizar o acesso de cada usuário à cobrança e persistir os efeitos de negócio de forma idempotente. A Public Key e o Access Token não substituem a autenticação dos usuários da sua API.

## Verificar

```powershell
dotnet build
dotnet test
```

Os testes usam um servidor HTTP de teste e um provedor simulado. Cobrem contratos de criação/consulta, validação de entrada, idempotência enviada ao provedor, erros, CORS e assinatura/reentrega de webhook; não criam cobranças externas. O arquivo `src/Billing.Api/Billing.Api.http` contém requisições para execução manual com uma credencial de teste configurada.

## Documentação oficial

- [Criar order](https://www.mercadopago.com.br/developers/pt/reference/online-payments/checkout-api/create-order/post)
- [Integração Pix](https://www.mercadopago.com.br/developers/pt/docs/checkout-api-orders/payment-integration/websites/pix)
- [Teste Pix com APRO](https://www.mercadopago.com.br/developers/pt/docs/checkout-api-orders/integration-test/pix)
- [Notificações de Orders](https://www.mercadopago.com.br/developers/pt/docs/checkout-api-orders/notifications)
- [Validador de webhook do SDK .NET](https://github.com/mercadopago/sdk-dotnet/blob/master/src/MercadoPago/Webhook/WebhookSignatureValidator.cs)
