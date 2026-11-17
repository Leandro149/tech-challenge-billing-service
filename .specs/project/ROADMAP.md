# Roadmap

1. Estrutura da API, configuração e Swagger.
2. Criação e consulta de Pix via HttpClient.
3. Webhook assinado com consulta do estado atual.
4. Testes HTTP com provedor simulado e guia de integração com frontend.
5. Publicação confirmada de estados de pagamento no RabbitMQ, Compose local e testes com broker real.
6. TC4-08: estratégia de Saga orquestrada definida em `docs/architecture/adr-0001-saga-os.md`.
7. TC4-09 a TC4-11: criação idempotente da OS, orçamento versionado e aprovação na Saga — implementação pendente nos serviços responsáveis, com persistência/inbox/outbox e recuperação.
