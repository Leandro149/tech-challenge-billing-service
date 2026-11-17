# CI/CD do Billing Service — develop

Escopo: workflow GitHub Actions para push e pull request em `develop`; execução manual prevista. Usuário escolheu entregar somente artefato pronto para deploy, sem GHCR nem implantação em servidor.

1. Classificar testes `Unit`, `Integration` e `RabbitMq`; adicionar testes unitários diretos do validador de assinatura. Arquivos: testes existentes, `WebhookSignatureValidatorTests.cs`, `tests/coverage.runsettings`, `scripts/Write-TestSummary.ps1`. Verificar filtros disjuntos, cobertura e falha em suíte vazia/ignorada.
2. Criar `.github/workflows/billing-ci-cd.yml` com build Release, suítes separadas, RabbitMQ isolado, relatórios TRX/cobertura, publish, smoke test e artefato. Verificar sintaxe com actionlint e executar os comandos da esteira.
3. Documentar uso brevemente no README e registrar resultado em STATE. Commitar na branch `develop` após verificar. Sem alterações no código de produção ou implantação nos outros microsserviços.

Aceite: nenhum teste existente removido; testes obrigatórios bloqueiam pacote; pull requests não publicam pacote de entrega; artefato só é disponibilizado após `/health` da aplicação publicada responder saudável. Testes usam provedor simulado, sem credenciais reais do Mercado Pago.

Verificação local: actionlint 1.7.12 sem erros; build Release com avisos tratados como erros; 17 testes unitários, 58 de integração e 3 com RabbitMQ real aprovados, sem ignorados, com relatórios de cobertura válidos. Suítes vazias/incompletas foram rejeitadas pelo script de resumo. Publish e health da aplicação publicada em Production validados; pacote tar.gz e checksum gerados. As três coletas locais foram verificadas separadamente porque o Coverlet instrumenta os mesmos binários; os jobs do GitHub usam runners isolados.
