param(
  [string]$Region = "eu-central-1",
  [string]$SecretId = "/peremetours/test/EASYTICKET_API_KEY"
)

$ErrorActionPreference = "Stop"

aws sts get-caller-identity --region $Region --no-cli-pager | Out-Null
if ($LASTEXITCODE -ne 0) {
  throw "AWS oturumu açık değil. Önce 'aws login' çalıştırın."
}

$secureApiKey = Read-Host "EasyTicket API anahtarı" -AsSecureString
$secretPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureApiKey)
try {
  $plainApiKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($secretPointer)
  if ([string]::IsNullOrWhiteSpace($plainApiKey)) {
    throw "EasyTicket API anahtarı boş olamaz."
  }

  aws secretsmanager put-secret-value `
    --secret-id $SecretId `
    --secret-string $plainApiKey `
    --region $Region `
    --no-cli-pager | Out-Null
  if ($LASTEXITCODE -ne 0) {
    throw "EasyTicket API anahtarı Secrets Manager'a kaydedilemedi."
  }

  Write-Host "EasyTicket API anahtarı güncellendi: $SecretId"
}
finally {
  $plainApiKey = $null
  [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($secretPointer)
}
