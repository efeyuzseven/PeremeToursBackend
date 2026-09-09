param(
  [string]$Region = "eu-central-1",
  [string]$StackName = "peremetours-test"
)

$ErrorActionPreference = "Stop"
$secretArn = aws cloudformation describe-stacks `
  --stack-name $StackName `
  --region $Region `
  --query "Stacks[0].Outputs[?OutputKey=='AdminPasswordSecretArn'].OutputValue | [0]" `
  --output text `
  --no-cli-pager
if ($LASTEXITCODE -ne 0) { throw "Admin parola kaynağı bulunamadı." }

aws secretsmanager get-secret-value `
  --secret-id $secretArn `
  --region $Region `
  --query SecretString `
  --output text `
  --no-cli-pager
