$ErrorActionPreference = 'Stop'
$apiProject = Join-Path $PSScriptRoot '..\src\Billing.Api\Billing.Api.csproj'

$token = Read-Host 'Access Token da aplicação de TESTE (entrada oculta)' -AsSecureString
if ($token.Length -eq 0) { throw 'O Access Token é obrigatório.' }
$webhook = Read-Host 'Assinatura secreta do webhook (Enter para configurar depois)' -AsSecureString

# Envia o JSON por stdin para não incluir os segredos nos argumentos do processo.
$localSecrets = @{
    'MercadoPago:AccessToken' = [System.Net.NetworkCredential]::new('', $token).Password
}
if ($webhook.Length -gt 0) {
    $localSecrets['MercadoPago:WebhookSecret'] = [System.Net.NetworkCredential]::new('', $webhook).Password
}
try {
    $localSecrets | ConvertTo-Json -Compress | dotnet user-secrets set --project $apiProject
    if ($LASTEXITCODE -ne 0) { throw 'Não foi possível salvar os User Secrets.' }
} finally {
    $localSecrets.Clear()
    $token.Dispose()
    $webhook.Dispose()
}
