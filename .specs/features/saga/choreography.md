# Integração mínima — Saga coreografada Fase 4

Esta decisão substitui a proposta de orquestração do ADR-0001 para a integração atual, conforme contrato fornecido pelo usuário. Preservar endpoints, eventos financeiros v1, assinatura de webhook, idempotência e testes existentes.

1. Contexto local persistente: OS por UniqueCode, orçamento, vínculo com pagamento, inbox e eventos de saída em SQLite; uma réplica. Verificar deduplicação e retomada após reinício.
2. Fluxo HTTP: gerar orçamento, solicitar aprovação via Pix, rejeitar explicitamente e consultar contexto. Aprovação distribuída depende de confirmação válida do pagamento; indisponibilidade não gera rejeição. Compensação usa refund/cancel da Orders API com chave estável e consulta de confirmação.
3. Mensageria: exchange topic durável `techchallenge.events`, fila `billing-service.events`, bindings `os.criada`/`execution.falhou`, saída `billing.aprovado`/`billing.reprovado`. ACK após commit, erro vai para DLQ própria, publicação confirmada de saída e recuperação persistida.
4. Verificação: testes existentes e novos para orçamento, correlação, confirmação de pagamento, falhas, refund pendente/confirmado, duplicatas e DLQ; executar `dotnet build` e `dotnet test`. Documentar apenas configuração e roteiro curto de teste.

JSON mínimo: `eventId` Guid, `ordemServicoUniqueCode` Guid e `occurredAt` UTC. Sem alterações nos outros microsserviços. Sem CQRS ou orquestrador. Parâmetros e credenciais aceitos por variáveis de ambiente; não usar credenciais reais nos testes.

Resultado: quatro entregas implementadas. `dotnet build`: zero avisos/erros; `BILLING_TEST_RABBITMQ=true dotnet test --no-build`: 63 testes aprovados, incluindo três testes com broker real. Mercado Pago permanece simulado nos testes, sem operações financeiras externas. API reiniciada e health/Swagger verificados; bindings da fila padrão confirmados no broker. Limites e roteiro em `docs/saga-minima.md`.
