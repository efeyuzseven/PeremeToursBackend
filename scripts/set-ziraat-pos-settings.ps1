param(
  [string]$Region = "eu-central-1",
  [string]$SecretId = "/peremetours/test/ZIRAAT_POS"
)

$ErrorActionPreference = "Stop"

aws sts get-caller-identity --region $Region --no-cli-pager | Out-Null
if ($LASTEXITCODE -ne 0) {
  throw "AWS oturumu açık değil. Önce 'aws login' çalıştırın."
}

$merchantId = Read-Host "Ziraat üye işyeri numarası"
$clientId = Read-Host "Ziraat client ID"
$apiUser = Read-Host "Ziraat API kullanıcısı"
$secureStoreKey = Read-Host "Ziraat store key" -AsSecureString
$secureApiPassword = Read-Host "Ziraat API parolası" -AsSecureString

if ([string]::IsNullOrWhiteSpace($merchantId) -or
    [string]::IsNullOrWhiteSpace($clientId) -or
    [string]::IsNullOrWhiteSpace($apiUser)) {
  throw "Üye işyeri, client ID ve API kullanıcısı boş olamaz."
}

$storeKeyPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureStoreKey)
$apiPasswordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureApiPassword)
$temporaryFile = New-TemporaryFile
try {
  $storeKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($storeKeyPointer)
  $apiPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($apiPasswordPointer)
  if ([string]::IsNullOrWhiteSpace($storeKey) -or [string]::IsNullOrWhiteSpace($apiPassword)) {
    throw "Store key ve API parolası boş olamaz."
  }

  $secretJson = @{
    merchantId = $merchantId.Trim()
    clientId = $clientId.Trim()
    storeKey = $storeKey
    apiUser = $apiUser.Trim()
    apiPassword = $apiPassword
  } | ConvertTo-Json -Compress
  [IO.File]::WriteAllText(
    $temporaryFile.FullName,
    $secretJson,
    [Text.UTF8Encoding]::new($false)
  )

  aws secretsmanager put-secret-value `
    --secret-id $SecretId `
    --secret-string "file://$($temporaryFile.FullName)" `
    --region $Region `
    --no-cli-pager | Out-Null
  if ($LASTEXITCODE -ne 0) {
    throw "Ziraat POS bilgileri Secrets Manager'a kaydedilemedi."
  }

  Write-Host "Ziraat POS bilgileri güncellendi: $SecretId"
  Write-Host "Ödeme henüz kapalıdır. Banka testi tamamlanmadan ZiraatPaymentEnabled=true kullanmayın."
}
finally {
  $storeKey = $null
  $apiPassword = $null
  $secretJson = $null
  [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($storeKeyPointer)
  [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($apiPasswordPointer)
  if (Test-Path -LiteralPath $temporaryFile.FullName) {
    Remove-Item -LiteralPath $temporaryFile.FullName -Force
  }
}
