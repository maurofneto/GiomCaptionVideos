# Cria a IAM Role usada pela Lambda watchman (giom-lambda-watchman-role).
# Rodar do diretorio infra/lambda-fila-watchman (onde estao trust-policy.json e permissions-policy.json).

aws iam create-role `
  --role-name giom-lambda-watchman-role `
  --assume-role-policy-document file://trust-policy.json `
  --region us-east-1

aws iam put-role-policy `
  --role-name giom-lambda-watchman-role `
  --policy-name giom-lambda-watchman-permissions `
  --policy-document file://permissions-policy.json `
  --region us-east-1

aws iam get-role --role-name giom-lambda-watchman-role --region us-east-1 --query "Role.Arn" --output text
