param(
  [string]$Region = "eu-central-1",
  [string]$StackName = "peremetours-test",
  [string]$GitHubRepositorySubject = "repo:efeyuzseven@163446299/PeremeToursBackend@1362469059:ref:refs/heads/main-prod",
  [ValidateSet("true", "false")]
  [string]$ZiraatPaymentEnabled = "false",
  [ValidateSet("true", "false")]
  [string]$MailSendingEnabled = "false"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location $repoRoot
$backendImageTag = "bootstrap"
$backendDesiredCount = "0"

aws sts get-caller-identity --region $Region --no-cli-pager | Out-Null
if ($LASTEXITCODE -ne 0) {
  throw "AWS oturumu açık değil. Önce 'aws login' çalıştırın."
}

$stack = aws cloudformation describe-stacks `
  --stack-name $StackName `
  --region $Region `
  --output json `
  --no-cli-pager 2>$null | ConvertFrom-Json
if ($LASTEXITCODE -eq 0 -and $stack.Stacks.Count -gt 0) {
  # Preserve existing activation flags unless the operator explicitly supplies a new value.
  foreach ($flagName in @("ZiraatPaymentEnabled", "MailSendingEnabled")) {
    if (-not $PSBoundParameters.ContainsKey($flagName)) {
      $existingFlag = $stack.Stacks[0].Parameters | Where-Object ParameterKey -eq $flagName
      if ($null -ne $existingFlag) { Set-Variable -Name $flagName -Value $existingFlag.ParameterValue }
    }
  }
  $outputs = @{}
  foreach ($output in $stack.Stacks[0].Outputs) {
    $outputs[$output.OutputKey] = $output.OutputValue
  }
  $service = aws ecs describe-services `
    --cluster $outputs.ClusterName `
    --services $outputs.BackendServiceName `
    --region $Region `
    --output json `
    --no-cli-pager | ConvertFrom-Json
  if ($LASTEXITCODE -ne 0) {
    throw "Mevcut ECS servisi okunamadı."
  }
  if ($service.services.Count -gt 0) {
    $backendDesiredCount = [string]$service.services[0].desiredCount
    $taskDefinition = aws ecs describe-task-definition `
      --task-definition $service.services[0].taskDefinition `
      --region $Region `
      --output json `
      --no-cli-pager | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) {
      throw "Mevcut ECS task tanımı okunamadı."
    }
    $container = $taskDefinition.taskDefinition.containerDefinitions |
      Where-Object name -eq "peremetours-backend"
    $imagePrefix = "$($outputs.BackendRepositoryUri):"
    if ($null -eq $container -or -not $container.image.StartsWith($imagePrefix)) {
      throw "Mevcut backend image etiketi belirlenemedi."
    }
    $backendImageTag = $container.image.Substring($imagePrefix.Length)
  }
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
  --parameter-overrides "GitHubRepositorySubject=$GitHubRepositorySubject" "ZiraatPaymentEnabled=$ZiraatPaymentEnabled" "MailSendingEnabled=$MailSendingEnabled" "BackendImageTag=$backendImageTag" "BackendDesiredCount=$backendDesiredCount" `
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
