# Integração Mercado Pago

- MP-01: POST /api/payments/pix deve validar dados e chave de idempotência, criar order e retornar ID, status e dados Pix.
- MP-02: GET /api/payments/{orderId} deve consultar a Orders API e retornar o mesmo contrato.
- MP-03: Webhook deve rejeitar assinatura inválida, consultar a order assinada e aceitar reentregas sem efeitos duplicados.
- MP-04: Frontend nas origens configuradas deve receber CORS; erros devem retornar ProblemDetails sem credenciais.
- MP-05: Credenciais não são versionadas; documentação deve explicar configuração, teste Pix APRO e limitações acadêmicas.

Decisões: .NET 10 instalado; controllers; HttpClient para API oficial; Mercado Pago como fonte de dados sem persistência local. Valor recebido do frontend apenas para demonstração, sem catálogo/pedido confiável disponível neste repositório.

Verificação: build, testes de integração sem cobrança externa, execução local e Swagger. Teste real depende de credencial de conta de teste configurada pelo usuário.

## Resultado

MP-01 a MP-05 verificados com build e 28 testes de integração usando provedor simulado. Health, Swagger UI e documento OpenAPI responderam HTTP 200 em execução local. A sintaxe do script de configuração foi validada. Não foram executadas chamadas reais nem armazenadas as credenciais da conversa.
