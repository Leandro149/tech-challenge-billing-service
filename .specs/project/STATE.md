# Estado

API acadêmica implementada em .NET 10, com criação/consulta de Pix, webhook validado, publicação no RabbitMQ, Swagger e CORS configurável.

Verificação da integração RabbitMQ (2026-11-16): 38 testes aprovados, incluindo dois testes opt-in com broker real via Compose. Cobertura: webhook até fila, mensagem persistente, publisher confirms/mandatory, rejeição de mensagem sem rota, recuperação, concorrência, falha/timeout, reentrega e assinatura inválida. Provedor de pagamento simulado; nenhuma cobrança externa criada.

Configuração externa gerenciada pelo usuário: Access Token da aplicação de teste nos User Secrets, origem do frontend e webhook HTTPS com seu segredo. Nenhuma credencial da conversa foi salva nem usada em chamadas externas neste trabalho.

Decisões: sem frontend e sem autenticação nesta demonstração. Contexto da Saga em SQLite local. Webhook consulta o estado e publica `payment.status.updated.v1` na exchange `billing.events`, fila `billing.payment-status`; só retorna 200 após confirmação. Falha de publicação retorna 503. Conexão/canal são reutilizados e recriados na próxima tentativa após falha. Frontend consulta GET para refletir mudanças.

Entrega: pode duplicar; eventId SHA256 é estável por tipo/order/status/detalhe. Consumidor externo deve deduplicar persistentemente, confirmar ACK após processamento e reconciliar estados conflitantes; sem garantia de ordenação ou histórico de transições. Sem consumidor de negócio/outbox no repositório; reentrega depende do Mercado Pago. RabbitMQ local usa credenciais públicas de demonstração, portas vinculadas a 127.0.0.1 e volume persistente.

Limites documentados no README: valor aceito da requisição apenas para o trabalho acadêmico; ambiente de teste depende da credencial, não do prefixo do token.

Fase 4: contrato posterior substituiu a proposta de orquestração por Saga coreografada. Billing consome `os.criada`/`execution.falhou` na fila própria `billing-service.events`, exchange `techchallenge.events`; publica `billing.aprovado` após pagamento válido e `billing.reprovado` somente por rejeição explícita. SQLite preserva contexto, deduplicação e saída transacional, com uma réplica. Compensação usa refund/cancel da Orders API, chave persistida e consulta de confirmação; falha permanece pendente/manual_review, sem evento falso de sucesso. Endpoints e evento financeiro v1 preservados; nenhum arquivo dos outros microsserviços alterado. Guia curto em `docs/saga-minima.md`.

Verificação da integração coreografada: build sem avisos/erros e 63 testes aprovados, incluindo três com RabbitMQ real. Testes preservados do fluxo anterior e cobertura nova de reinício, chaves, orçamento, pagamento, compensação pendente/confirmada, publicação e DLQ. Nenhuma operação financeira externa foi executada. API atualizada em localhost:5080 e fila/bindings padrão confirmados no broker.
