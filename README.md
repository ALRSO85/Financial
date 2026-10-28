# Financial

API demonstrativa em C#/.NET para registrar créditos e débitos e consultar saldo atual ou histórico. O exemplo prioriza lançamento contábil imutável, idempotência e proteção contra saldo negativo.

## Requisitos

- .NET SDK 10.0.402 (ou patch posterior da linha 10.0)
- PowerShell para executar o gerador de token local (`.ps1`)
- Windows, Linux ou macOS

## Executar localmente

```bash
dotnet restore
```

A API usa o provider `json` por padrão e cria `data/financial.json` no diretório do projeto. O caminho pode ser alterado por `Financial:FilePath`; `Financial:StorageProvider` permite selecionar o provider registrado. Neste protótipo, somente `json` está disponível. No PowerShell, inicie-a pelo script, que configura valores JWT temporários apenas para desenvolvimento:

No primeiro terminal:

```powershell
.\scripts\Start-FinancialApi.ps1
```

Em um segundo terminal, gere um token curto com os mesmos valores. O script `.ps1` requer PowerShell; se preferir outro shell, gere um JWT equivalente com o mesmo emissor, audiência, chave e claim `sub`.

```powershell
$env:Jwt__Issuer = "financial-local"
$env:Jwt__Audience = "financial-api"
$env:Jwt__SigningKey = "local-only-change-this-key-to-a-random-32-byte-value"
$token = .\scripts\New-LocalJwt.ps1 -Subject "cliente-123"
```

A API escuta em `http://localhost:5080`. No ambiente `Development`, a documentação interativa fica em `/swagger` e o documento OpenAPI em `/swagger/v1/swagger.json`. O botão **Authorize** aceita o bearer token. O caminho do JSON pode ser alterado pela configuração `Financial:FilePath`.

Criar conta:

```powershell
curl.exe -X POST http://localhost:5080/accounts -H "Authorization: Bearer $token"
```

O `201 Created` retorna a URL da conta, disponível somente para o titular:

```powershell
curl.exe -H "Authorization: Bearer $token" http://localhost:5080/accounts/{accountId}
```

Registrar crédito ou débito (valores inteiros em centavos):

```powershell
curl.exe -X POST http://localhost:5080/accounts/{accountId}/transactions -H "Authorization: Bearer $token" -H "Content-Type: application/json" -d '{"type":"credit","amountInCents":12500,"idempotencyKey":"pagamento-abc-001","description":"depósito"}'
```

Substitua `{accountId}` pelo identificador retornado na criação da conta.

O `201 Created` retorna a URL do lançamento criado. Ela pode ser consultada pelo mesmo titular:

```powershell
curl.exe -H "Authorization: Bearer $token" http://localhost:5080/accounts/{accountId}/transactions/{transactionId}
```

Consultar saldo atual ou posição até um instante ISO-8601:

```powershell
curl.exe -H "Authorization: Bearer $token" http://localhost:5080/accounts/{accountId}/balance
curl.exe -H "Authorization: Bearer $token" "http://localhost:5080/accounts/{accountId}/balance?at=2026-10-28T09:00:00-03:00"
curl.exe -H "Authorization: Bearer $token" http://localhost:5080/customers/me/balance
curl.exe -H "Authorization: Bearer $token" "http://localhost:5080/customers/me/balance?at=2026-10-28T09:00:00-03:00"
```

Ao informar `at`, use ISO 8601 com fuso horário explícito. Os exemplos usam o horário de Brasília (`-03:00`); o sistema compara e persiste os instantes em UTC. O JSON da API mantém ISO 8601, e a aplicação cliente pode formatar a exibição conforme `pt-BR`.

A conta é criada com o `sub` do token; somente tokens com o mesmo `sub` podem consultar ou movimentar essa conta. A API valida assinatura, emissor, audiência e validade do token. Ela não emite tokens nem autentica credenciais de usuário: em produção, configure `Jwt:Authority`, `Jwt:Issuer` e `Jwt:Audience` para um provedor de identidade confiável e remova `Jwt:SigningKey`. Os modos `Authority` e `SigningKey` são mutuamente exclusivos. O script local usa uma chave simétrica apenas para testar; mantenha a chave fora do repositório e não a reutilize em produção.

Os comandos HTTP acima usam `curl.exe` no Windows. Em Linux ou macOS, use `curl` no lugar; para executar os scripts `.ps1`, instale PowerShell 7 (`pwsh`).

## Testes

```bash
dotnet test
```

Os testes cobrem autenticação JWT, isolamento e preservação exata da referência do titular, validação do fuso em consultas históricas, criação e consulta de contas e lançamentos, crédito/débito, saldo insuficiente, idempotência sequencial e concorrente, consolidação entre contas, persistência após reiniciar o serviço, relógio regressivo e falha fechada diante de JSON corrompido ou ledger inconsistente. Cada teste usa seu próprio arquivo temporário.

## Organização do código

```text
src/
  Financial.Domain/         agregado Account, Money e FinancialEntry
  Financial.Application/    casos de uso, contratos e porta do repositório
  Financial.Infrastructure/ adapter de persistência JSON
  Financial.Api/            endpoints HTTP, composição e Swagger
tests/
  Financial.Api.Tests/      testes automatizados dos casos de uso
```

O domínio não depende de HTTP ou armazenamento. A aplicação depende do domínio e define `IFinancialRepository`; a infraestrutura implementa essa porta e a API conecta as dependências. A documentação de decisões e limites fica em [`docs/architecture.md`](docs/architecture.md).

## Guia rápido para avaliação

| Tema | Onde conferir |
| --- | --- |
| Regras de conta, valores e lançamentos | `src/Financial.Domain/` |
| Crédito, débito, idempotência e saldos atuais/históricos | `src/Financial.Application/FinancialApplicationService.cs` |
| Persistência JSON e validação do snapshot | `src/Financial.Infrastructure/JsonFinancialRepository.cs` |
| JWT, endpoints e Swagger | `src/Financial.Api/` |
| Testes de regras e contrato HTTP | `tests/Financial.Api.Tests/` |
| Decisões, limites e evolução para produção | [`docs/architecture.md`](docs/architecture.md) |

## Decisões de arquitetura

- **Ledger append-only:** cada operação aprovada gera uma entrada de valor assinado e nunca se altera/exclui; correções futuras devem ser novos lançamentos compensatórios e auditáveis.
- **Centavos inteiros:** evita arredondamento binário de ponto flutuante. `long` tem limite finito, validado por aritmética checked.
- **Moeda assumida:** os valores em centavos representam reais (BRL). O protótipo não aceita outras moedas nem faz conversão cambial.
- **Persistência local:** JSON foi escolhido para deixar o exercício autocontido, sem exigir a instalação de um servidor ou pacote de banco, e para facilitar inspeção e isolamento dos testes. As gravações são serializadas dentro do processo, escritas em arquivo temporário e depois substituídas no mesmo diretório.
- **Saldo materializado:** o saldo atual está no agregado `Account`; como a persistência JSON carrega o arquivo inteiro, a leitura e escrita crescem com o ledger. Um banco relacional permitirá consultas indexadas e snapshots históricos se o volume justificar.
- **Concorrência neste protótipo:** um `SemaphoreSlim` global serializa todas as chamadas na instância, inclusive consultas. Isso simplifica a proteção do arquivo JSON, mas limita a capacidade sob concorrência. Débitos não podem produzir saldo negativo.
- **Idempotência:** chave única por conta; repetição com os mesmos campos devolve o lançamento original, e conteúdo diferente na mesma chave retorna 409.
- **DDD e Clean Architecture proporcionais:** `Account` protege regras do agregado; `Money` encapsula centavos; `FinancialEntry` é imutável. Casos de uso dependem da porta `IFinancialRepository`, e JSON fica no adapter de infraestrutura. A seleção e o registro do provider ficam em `FinancialPersistenceRegistration`; um adapter relacional futuro pode implementar a mesma porta sem alterar os casos de uso ou o domínio. Não há mensageria ou serviços distribuídos sem necessidade demonstrada.

## Limites e evolução para produção

O arquivo JSON foi escolhido para manter o exercício simples e autocontido; não é recomendação para um banco digital. A gravação em arquivo inteiro tem custo crescente com o tamanho do ledger. O bloqueio não coordena processos ou instâncias diferentes, e a substituição de arquivo não oferece as garantias de transação, recuperação e durabilidade de um banco relacional. Para múltiplas instâncias, usaria PostgreSQL ou SQL Server gerenciado, migrations versionadas, isolamento/locks de linha ou atualização condicional atômica, backups testados e monitoramento. Garantiria unicidade idempotente no banco, retentativas limitadas com backoff apenas para falhas transitórias, timeout e circuit breaker nas dependências externas. Não retornaria sucesso antes do commit.

O saldo consolidado aqui significa saldo contábil resultante de lançamentos ordenados por `OccurredAt` (UTC). Requisitos de data-valor, lançamentos retroativos, ordenação total, estornos, limites/regulação e saldo disponível versus contábil precisam ser definidos pelo produto. Para histórico com alta taxa de consulta, snapshots devem ser reconstruíveis e conciliados com o ledger.

Antes de exposição externa, acrescentaria TLS, gestão segura de chaves, criptografia e política de retenção, auditoria de acesso, rate limiting, limites de payload, métricas/traces/logs sem dados sensíveis, health checks, migrations no deploy, backup/restore e reconciliação operacional. A API usa `sub` como referência opaca do titular; não coloque CPF ou outros dados pessoais nesse claim.

## Contrato resumido

| Operação | Resultado |
| --- | --- |
| `POST /accounts` | Cria conta a partir de referência opaca do titular |
| `GET /accounts/{id}` | Consulta os dados da conta do titular |
| `POST /accounts/{id}/transactions` | Registra `credit` ou `debit`, em centavos, com `idempotencyKey` |
| `GET /accounts/{id}/transactions/{transactionId}` | Consulta um lançamento da conta |
| `GET /accounts/{id}/balance` | Saldo atual |
| `GET /accounts/{id}/balance?at={ISO-8601}` | Saldo até o instante informado |
| `GET /customers/me/balance` | Saldo consolidado das contas do titular autenticado, atual ou histórico |

Erros de validação retornam `400`, recurso ausente `404`, conflito/idempotência `409` e saldo insuficiente/overflow `422`. Erros da API usam `ProblemDetails` (`application/problem+json`); falhas inesperadas são tratadas pelo middleware padrão de exceções.

## Publicação

O código está publicado em [github.com/ALRSO85/Financial](https://github.com/ALRSO85/Financial), na branch `main`. A integração contínua executa restore, build e testes a cada push e pull request.
