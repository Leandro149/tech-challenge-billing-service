# Billing Service

API acadêmica em C# / ASP.NET Core 10 para conectar o frontend ao Pix do Mercado Pago via Orders API.

Escopo: criar cobrança, consultar estado atual, validar e receber webhook, publicar estados de pagamento no RabbitMQ, CORS configurável e Swagger.
Credenciais ficam em User Secrets ou variáveis de ambiente. Sem frontend, banco local, cartões ou autenticação nesta demonstração.
