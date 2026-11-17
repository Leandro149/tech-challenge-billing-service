# TC4-08 — Definição da estratégia de Saga

Registro histórico da proposta original. Para a implementação atual, o contrato posterior exige coreografia; ver [escopo atualizado](choreography.md) e [fluxo implementado](../../../docs/saga-minima.md).

## Objetivo e entrega

Definir a estratégia para o Épico 2 (OS, orçamento e aprovação) e a participação futura do billing. Documento de decisão: [ADR-0001](../../../docs/architecture/adr-0001-saga-os.md).

Escopo desta entrega: decisão arquitetural. Não inclui um orquestrador executável, persistência ou consumidores novos nesta API.

## Requisitos e verificação

| ID | Requisito | Evidência |
| --- | --- | --- |
| SAGA-01 | Escolher orquestração/coreografia e responsável pelo fluxo. | ADR: Decisão e Responsabilidades. |
| SAGA-02 | Definir estados e sequência de OS, diagnóstico, orçamento e aprovação. | ADR: Fluxo e estados. |
| SAGA-03 | Definir correlação, contratos versionados, entrega e duplicatas. | ADR: Contratos e correlação; Consistência, persistência e recuperação. |
| SAGA-04 | Definir transações locais, inbox/outbox, concorrência e retomada após reinício. | ADR: Consistência, persistência e recuperação. |
| SAGA-05 | Definir falhas, retentativas, prazos, compensação e intervenção. | ADR: Compensações e falhas de negócio. |
| SAGA-06 | Preservar o evento financeiro v1 e separar aprovação de pagamento. | ADR: Integração com o contrato atual do billing. |
| SAGA-07 | Separar a definição entregue das etapas de execução pendentes. | ADR: Observabilidade e critérios de aceite; Entrega e limites. |

## Estado

- TC4-08: definição concluída e documentada.
- TC4-09: implementação da criação da OS na Saga pendente.
- TC4-10: implementação do orçamento na Saga pendente.
- TC4-11: implementação da aprovação na Saga pendente.

Validação desta entrega: coerência com os contratos e fronteiras atuais, revisão dos caminhos de falha e verificação de links locais. Não há alteração de código de produção nem alegação de testes executáveis da Saga.
