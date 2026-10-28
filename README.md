# Ledger bancário

API demonstrativa em C#/.NET para registrar créditos e débitos e consultar saldo atual ou histórico. O exemplo prioriza lançamento contábil imutável, idempotência e proteção contra saldo negativo.

## Requisitos

- .NET SDK 10.0.402 (ou patch posterior da linha 10.0)
- Windows, Linux ou macOS

## Executar localmente

```bash
dotnet restore
```

A API exige configuração JWT e cria `data/ledger.json` no diretório do projeto. No PowerShell, configure credenciais temporárias para a execução local e gere um token curto:

```powershell
$env:Jwt__Issuer = "ledger-local"
$env:Jwt__Audience = "ledger-api"
$env:Jwt__SigningKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$token = .\scripts\New-LocalJwt.ps1 -Subject "cliente-123"
dotnet run --project src/Ledger.Api
```

No ambiente `Development`, a documentação interativa fica em `/swagger` e o documento OpenAPI em `/swagger/v1/swagger.json`. O botão **Authorize** aceita o bearer token. O caminho do JSON pode ser alterado pela configuração `Ledger:FilePath`.

Criar conta:

```bash
curl -X POST http://localhost:5000/accounts -H "Authorization: Bearer $token"
```

Registrar crédito ou débito (valores inteiros em centavos):

```bash
curl -X POST http://localhost:5000/accounts/{accountId}/transactions \
  -H "Authorization: Bearer $token" -H "Content-Type: application/json" \
  -d '{"type":"credit","amountInCents":12500,"idempotencyKey":"pagamento-abc-001","description":"depósito"}'
```

Consultar saldo atual ou posição até um instante ISO-8601:

```bash
curl http://localhost:5000/accounts/{accountId}/balance
curl "http://localhost:5000/accounts/{accountId}/balance?at=2026-10-28T12:00:00Z"
```

Inclua `-H "Authorization: Bearer $token"` nessas consultas também. A conta é criada com o `sub` do token; somente tokens com o mesmo `sub` podem consultar ou movimentar essa conta. A API valida assinatura, emissor, audiência e validade do token. Ela não emite tokens nem autentica credenciais de usuário: em produção, configure `Jwt:Authority`, `Jwt:Issuer` e `Jwt:Audience` para um provedor de identidade confiável. O script local usa uma chave simétrica apenas para testar; mantenha a chave fora do repositório e não a reutilize em produção.

## Testes

```bash
dotnet test
```

Os testes cobrem autenticação JWT, isolamento por titular, crédito/débito, saldo insuficiente, repetição idempotente, consulta histórica e persistência após reiniciar o serviço. Cada teste usa seu próprio arquivo temporário.

## Organização do código

```text
src/
  Ledger.Domain/         agregado Account, Money e LedgerEntry
  Ledger.Application/    casos de uso, contratos e porta do repositório
  Ledger.Infrastructure/ adapter de persistência JSON
  Ledger.Api/            endpoints HTTP, composição e Swagger
tests/
  Ledger.Api.Tests/      testes automatizados dos casos de uso
```

O domínio não depende de HTTP ou armazenamento. A aplicação depende do domínio e define `ILedgerRepository`; a infraestrutura implementa essa porta e a API conecta as dependências. A documentação de decisões e limites fica em [`docs/architecture.md`](docs/architecture.md).

## Decisões de arquitetura

- **Ledger append-only:** cada operação aprovada gera uma entrada de valor assinado e nunca se altera/exclui; correções futuras devem ser novos lançamentos compensatórios e auditáveis.
- **Centavos inteiros:** evita arredondamento binário de ponto flutuante. `long` tem limite finito, validado por aritmética checked.
- **Persistência local:** os dados ficam em um arquivo JSON. As gravações são serializadas dentro do processo, escritas em arquivo temporário e depois substituídas no mesmo diretório.
- **Saldo materializado:** o saldo atual está no agregado `Account`; como a persistência JSON carrega o arquivo inteiro, a leitura e escrita crescem com o ledger. Um banco relacional permitirá consultas indexadas e snapshots históricos se o volume justificar.
- **Concorrência neste protótipo:** um `SemaphoreSlim` serializa operações dentro de uma instância. Débitos não podem produzir saldo negativo.
- **Idempotência:** chave única por conta; repetição com os mesmos campos devolve o lançamento original, e conteúdo diferente na mesma chave retorna 409.
- **DDD e Clean Architecture proporcionais:** `Account` protege regras do agregado; `Money` encapsula centavos; `LedgerEntry` é imutável. Casos de uso dependem da porta `ILedgerRepository`, e JSON fica no adapter de infraestrutura. Não há mensageria ou serviços distribuídos sem necessidade demonstrada.

## Limites e evolução para produção

O arquivo JSON foi escolhido para manter o exercício simples e autocontido; não é recomendação para um banco digital. A gravação em arquivo inteiro tem custo crescente com o tamanho do ledger. O bloqueio não coordena processos ou instâncias diferentes, e a substituição de arquivo não oferece as garantias de transação, recuperação e durabilidade de um banco relacional. Para múltiplas instâncias, usaria PostgreSQL ou SQL Server gerenciado, migrations versionadas, isolamento/locks de linha ou atualização condicional atômica, backups testados e monitoramento. Garantiria unicidade idempotente no banco, retentativas limitadas com backoff apenas para falhas transitórias, timeout e circuit breaker nas dependências externas. Não retornaria sucesso antes do commit.

O saldo consolidado aqui significa saldo contábil resultante de lançamentos ordenados por `OccurredAt` (UTC). Requisitos de data-valor, lançamentos retroativos, ordenação total, estornos, limites/regulação e saldo disponível versus contábil precisam ser definidos pelo produto. Para histórico com alta taxa de consulta, snapshots devem ser reconstruíveis e conciliados com o ledger.

Antes de exposição externa, acrescentaria TLS, gestão segura de chaves, criptografia e política de retenção, auditoria de acesso, rate limiting, limites de payload, métricas/traces/logs sem dados sensíveis, health checks, migrations no deploy, backup/restore e reconciliação operacional. A API usa `sub` como referência opaca do titular; não coloque CPF ou outros dados pessoais nesse claim.

## Contrato resumido

| Operação | Resultado |
| --- | --- |
| `POST /accounts` | Cria conta a partir de referência opaca do titular |
| `POST /accounts/{id}/transactions` | Registra `credit` ou `debit`, em centavos, com `idempotencyKey` |
| `GET /accounts/{id}/balance` | Saldo atual |
| `GET /accounts/{id}/balance?at={ISO-8601}` | Saldo até o instante informado |

Erros de validação retornam 400, conta ausente 404, conflito/idempotência 409 e saldo insuficiente/overflow 422.

## Publicação

O repositório local contém o projeto pronto para versionamento. Para cumprir a publicação pública, crie um repositório GitHub público e configure-o como `origin`, depois envie a branch principal. Não há remote GitHub configurado neste checkout.
