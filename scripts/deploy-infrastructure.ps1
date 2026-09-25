param(
  [string]$Region = "eu-central-1",
  [string]$StackName = "peremetours-test",
  [string]$GitHubRepositorySubject = "repo:efeyuzseven@163446299/PeremeToursBackend@1362469059:ref:refs/heads/main-prod",
  [ValidateSet("true", "false")]
  [string]$ZiraatPaymentEnabled = "false"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location $repoRoot

aws sts get-caller-identity --region $Region --no-cli-pager | Out-Null
if ($LASTEXITCODE -ne 0) {
  throw "AWS oturumu açık değil. Önce 'aws login' çalıştırın."
}

dotnet restore PeremeTours.slnx
if ($LASTEXITCODE -ne 0) { throw "Paketler geri yüklenemedi." }
dotnet build PeremeTours.slnx --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "Backend derlenemedi." }
dotnet test PeremeTours.slnx --configuration Release --no-build
if ($LASTEXITCODE -ne 0) { throw "Testler başarısız." }

$template = Join-Path $repoRoot "deploy\aws\peremetours-test.yml"
aws cloudformation validate-template --template-body "file://$template" --region $Region --no-cli-pager | Out-Null
if ($LASTEXITCODE -ne 0) { throw "CloudFormation şablonu geçersiz." }

aws cloudformation deploy `
  --template-file $template `
  --stack-name $StackName `
  --region $Region `
  --capabilities CAPABILITY_NAMED_IAM `
  --parameter-overrides "GitHubRepositorySubject=$GitHubRepositorySubject" "ZiraatPaymentEnabled=$ZiraatPaymentEnabled" `
  --tags Project=PeremeTours Environment=Test `
  --no-fail-on-empty-changeset `
  --no-cli-pager
if ($LASTEXITCODE -ne 0) { throw "AWS altyapı dağıtımı başarısız." }

aws cloudformation describe-stacks `
  --stack-name $StackName `
  --region $Region `
  --query "Stacks[0].Outputs" `
  --output table `
  --no-cli-pager
