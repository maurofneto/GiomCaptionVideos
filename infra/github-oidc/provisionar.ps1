<#
Provisiona a federação OIDC GitHub Actions -> AWS usada por .github/workflows/ci-cd.yml.

Cria (idempotente):
  - IAM OIDC provider token.actions.githubusercontent.com
  - role giom-github-deploy-dev   (confiável só para o environment "dev")
  - role giom-github-deploy-prod  (confiável só para o environment "prod")
  Cada role: push no ECR giom-* + ecs:UpdateService apenas nos services do próprio ambiente.

SEM -Apply só imprime o que faria (dry-run). Com -Apply altera a conta AWS — confirme antes.
Uso:  pwsh infra/github-oidc/provisionar.ps1            # dry-run
      pwsh infra/github-oidc/provisionar.ps1 -Apply
#>
param(
    [switch]$Apply,
    [string]$Repo    = 'maurofneto/GiomCaptionVideos',
    # Este repo usa o subject IMUTÁVEL do OIDC (ids numéricos de dono e repo). Confirme com:
    #   gh api repos/<repo>/actions/oidc/customization/sub  -> sub_claim_prefix
    [string]$SubPrefix = 'repo:maurofneto@1536870/GiomCaptionVideos@1426143006',
    [string]$Region  = 'us-east-1',
    [string]$Cluster = 'giom-cluster'
)

$ErrorActionPreference = 'Stop'
$env:MSYS_NO_PATHCONV = '1'   # ver docs/ENGINEERING_STANDARDS.md (AWS CLI no Windows)

$account = aws sts get-caller-identity --query Account --output text
$oidcUrl = 'token.actions.githubusercontent.com'
$oidcArn = "arn:aws:iam::${account}:oidc-provider/$oidcUrl"

$etapas = 'transcricao', 'resumo', 'categorizacao', 'embedding', 'orquestracao', 'agenteinstituto'
$ambientes = @{
    dev  = $etapas | ForEach-Object { "giom-$_-dev-service" }
    prod = $etapas | ForEach-Object { "giom-$_-service" }
}

function Invoke-Step([string]$descricao, [scriptblock]$acao) {
    if ($Apply) {
        Write-Host "-> $descricao"
        & $acao
        # `aws` é um executável nativo: ErrorActionPreference não o intercepta.
        if ($LASTEXITCODE -ne 0) { throw "Falhou: $descricao (aws exit $LASTEXITCODE)" }
    } else { Write-Host "[dry-run] $descricao" }
}

# 1) OIDC provider (a thumbprint não é mais exigida pela AWS para este issuer)
$existe = aws iam list-open-id-connect-providers --query "OpenIDConnectProviderList[?Arn=='$oidcArn'].Arn" --output text
if (-not $existe) {
    Invoke-Step "criar OIDC provider $oidcUrl" {
        aws iam create-open-id-connect-provider --url "https://$oidcUrl" --client-id-list sts.amazonaws.com | Out-Null
    }
} else { Write-Host "OIDC provider já existe: $oidcArn" }

# 2) Roles por ambiente
foreach ($amb in 'dev', 'prod') {
    $role = "giom-github-deploy-$amb"

    $trust = @{
        Version = '2012-10-17'
        Statement = @(@{
            Effect = 'Allow'
            Principal = @{ Federated = $oidcArn }
            Action = 'sts:AssumeRoleWithWebIdentity'
            Condition = @{
                StringEquals = @{
                    "${oidcUrl}:aud" = 'sts.amazonaws.com'
                    # sub do job com `environment: <amb>` (o environment, por sua vez, só aceita a branch certa)
                    "${oidcUrl}:sub" = "${SubPrefix}:environment:$amb"
                }
            }
        })
    } | ConvertTo-Json -Depth 10 -Compress

    $services = $ambientes[$amb] | ForEach-Object { "arn:aws:ecs:${Region}:${account}:service/$Cluster/$_" }
    $policy = @{
        Version = '2012-10-17'
        Statement = @(
            @{ Effect = 'Allow'; Action = 'ecr:GetAuthorizationToken'; Resource = '*' },
            @{ Effect = 'Allow'
               Action = 'ecr:BatchCheckLayerAvailability', 'ecr:PutImage', 'ecr:InitiateLayerUpload',
                        'ecr:UploadLayerPart', 'ecr:CompleteLayerUpload', 'ecr:BatchGetImage'
               Resource = "arn:aws:ecr:${Region}:${account}:repository/giom-*" },
            @{ Effect = 'Allow'; Action = 'ecs:UpdateService'; Resource = @($services) }
        )
    } | ConvertTo-Json -Depth 10 -Compress

    $trustFile  = New-TemporaryFile; $policyFile = New-TemporaryFile
    Set-Content -Path $trustFile  -Value $trust  -Encoding ascii
    Set-Content -Path $policyFile -Value $policy -Encoding ascii

    $roleExiste = aws iam list-roles --query "Roles[?RoleName=='$role'].RoleName" --output text
    if (-not $roleExiste) {
        Invoke-Step "criar role $role (trust: environment:$amb)" {
            aws iam create-role --role-name $role --assume-role-policy-document "file://$trustFile" `
                --description "Deploy do GitHub Actions ($amb)" | Out-Null
        }
    } else {
        Invoke-Step "atualizar trust policy de $role" {
            aws iam update-assume-role-policy --role-name $role --policy-document "file://$trustFile"
        }
    }
    Invoke-Step "aplicar inline policy giom-deploy em $role ($($services.Count) ECS services)" {
        aws iam put-role-policy --role-name $role --policy-name giom-deploy --policy-document "file://$policyFile"
    }
    Write-Host "   ARN: arn:aws:iam::${account}:role/$role"
    Remove-Item $trustFile, $policyFile -ErrorAction SilentlyContinue
}
