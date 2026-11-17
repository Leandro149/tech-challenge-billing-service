# Roadmap

1. Estrutura da API, configuração e Swagger.
2. Criação e consulta de Pix via HttpClient.
3. Webhook assinado com consulta do estado atual.
4. Testes HTTP com provedor simulado e guia de integração com frontend.
5. Publicação confirmada de estados de pagamento no RabbitMQ, Compose local e testes com broker real.
6. TC4-08: proposta histórica de Saga orquestrada em `docs/architecture/adr-0001-saga-os.md`, substituída pelo contrato coreografado posterior.
7. Billing da Saga coreografada: registro de OS por evento, orçamento mínimo, decisão vinculada a Pix, compensação confirmada, persistência/inbox/outbox e DLQ. Integração dos demais participantes permanece nos seus serviços.
