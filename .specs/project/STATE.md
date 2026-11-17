# Estado

API acadêmica implementada em .NET 10, com criação/consulta de Pix, webhook validado, publicação no RabbitMQ, Swagger e CORS configurável.

Verificação da integração RabbitMQ (2026-11-16): 38 testes aprovados, incluindo dois testes opt-in com broker real via Compose. Cobertura: webhook até fila, mensagem persistente, publisher confirms/mandatory, rejeição de mensagem sem rota, recuperação, concorrência, falha/timeout, reentrega e assinatura inválida. Provedor de pagamento simulado; nenhuma cobrança externa criada.

Configuração externa gerenciada pelo usuário: Access Token da aplicação de teste nos User Secrets, origem do frontend e webhook HTTPS com seu segredo. Nenhuma credencial da conversa foi salva nem usada em chamadas externas neste trabalho.

Decisões: sem banco local, sem frontend e sem autenticação nesta demonstração. Webhook consulta o estado e publica `payment.status.updated.v1` na exchange `billing.events`, fila `billing.payment-status`; só retorna 200 após confirmação. Falha de publicação retorna 503. Conexão/canal são reutilizados e recriados na próxima tentativa após falha. Frontend consulta GET para refletir mudanças.

Entrega: pode duplicar; eventId SHA256 é estável por tipo/order/status/detalhe. Consumidor externo deve deduplicar persistentemente, confirmar ACK após processamento e reconciliar estados conflitantes; sem garantia de ordenação ou histórico de transições. Sem consumidor de negócio/outbox no repositório; reentrega depende do Mercado Pago. RabbitMQ local usa credenciais públicas de demonstração, portas vinculadas a 127.0.0.1 e volume persistente.

Limites documentados no README: valor aceito da requisição apenas para o trabalho acadêmico; ambiente de teste depende da credencial, não do prefixo do token.

TC4-08: estratégia definida em `docs/architecture/adr-0001-saga-os.md`, com requisitos em `.specs/features/saga/spec.md`. Saga orquestrada no serviço de OS; orçamento pode permanecer no mesmo agregado/transação local. Estados, contratos futuros, inbox/outbox, concorrência, prazos e compensações documentados. Billing v1 é preservado e correlacionado por vínculo persistido; aprovação do orçamento é separada de pagamento. TC4-09 a TC4-11 e integração financeira executável continuam pendentes. Não foi adicionado orquestrador/consumer/banco nesta API. O projeto local relacionado `tech_challenge` foi apenas consultado para alinhar o domínio.
