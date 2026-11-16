# Estado

API acadêmica implementada em .NET 10, com criação/consulta de Pix, webhook validado, Swagger e CORS configurável.

Verificação: build sem avisos/erros; 28 testes aprovados com provedor simulado; health/Swagger/OpenAPI responderam 200; sintaxe do script PowerShell validada.

Configuração externa pendente do usuário: salvar Access Token da aplicação de teste nos User Secrets, configurar origem do frontend se diferente das origens locais e cadastrar webhook HTTPS com seu segredo. Credenciais compartilhadas na conversa não foram salvas nem usadas em chamadas externas.

Decisões: sem banco local, sem frontend e sem autenticação nesta demonstração. Webhook consulta e registra o estado, sem atualizar pedidos de outro serviço. Frontend consulta GET para refletir mudanças.

Limites documentados no README: valor aceito da requisição apenas para o trabalho acadêmico; ambiente de teste depende da credencial, não do prefixo do token.
