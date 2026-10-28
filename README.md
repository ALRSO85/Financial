# Ledger bancário

API demonstrativa em C#/.NET para registrar créditos e débitos e consultar saldo atual ou histórico. O exemplo prioriza lançamento contábil imutável, idempotência e proteção contra saldo negativo.

## Requisitos

- .NET SDK 10.0.402 (ou patch posterior da linha 10.0)
- Windows, Linux ou macOS

## Executar localmente

```bash
dotnet restore
dotnet run --project src/Ledger.Api
```

A API cria `data/ledger.json` no diretório de execução e escuta na URL indicada pelo console (por padrão, `http://localhost:5000` ou uma porta atribuída pelo ambiente). O caminho pode ser alterado pela configuração `Ledger:FilePath`.

Criar conta:

```bash
curl -X POST http://localhost:5000/accounts -H "Content-Type: application/json" -d '{"ownerReference":"cliente-123"}'
```

Registrar crédito ou débito (valores inteiros em centavos):

```bash
curl -X POST http://localhost:5000/accounts/{accountId}/transactions \
  -H "Content-Type: application/json" \
  -d '{"type":"credit","amountInCents":12500,"idempotencyKey":"pagamento-abc-001","description":"depósito"}'
```

Consultar saldo atual ou posição até um instante ISO-8601:

```bash
curl http://localhost:5000/accounts/{accountId}/balance
curl "http://localhost:5000/accounts/{accountId}/balance?at=2026-10-28T12:00:00Z"
```

## Testes

```bash
dotnet test
```

Os testes cobrem crédito/débito, saldo insuficiente, repetição idempotente, consulta histórica e persistência após reiniciar o serviço. Cada teste usa seu próprio arquivo temporário.

## Decisões de arquitetura

- **Ledger append-only:** cada operação aprovada gera uma entrada de valor assinado e nunca se altera/exclui; correções futuras devem ser novos lançamentos compensatórios e auditáveis.
- **Centavos inteiros:** evita arredondamento binário de ponto flutuante. `long` tem limite finito, validado por aritmética checked.
- **Persistência local:** os dados ficam em um arquivo JSON. As gravações são serializadas dentro do processo, escritas em arquivo temporário e depois substituídas no mesmo diretório.
- **Saldo materializado:** leitura atual é O(1). O ledger é fonte para reconstrução e consultas históricas; índices apoiam agregação por conta e instante. Para volumes maiores, introduzir snapshots/checkpoints históricos sem perder trilha auditável.
- **Concorrência neste protótipo:** um `SemaphoreSlim` serializa operações dentro de uma instância. Débitos não podem produzir saldo negativo.
- **Idempotência:** chave única por conta; repetição com os mesmos campos devolve o lançamento original, e conteúdo diferente na mesma chave retorna 409.
- **Modularidade proporcional:** API, domínio, persistência e serviço estão separados por responsabilidade, sem adicionar mensageria ou serviços distribuídos antes de haver necessidade operacional comprovada.

## Limites e evolução para produção

O arquivo JSON foi escolhido para manter o exercício simples e autocontido; não é recomendação para um banco digital. A gravação em arquivo inteiro tem custo crescente com o tamanho do ledger. O bloqueio não coordena processos ou instâncias diferentes, e a substituição de arquivo não oferece as garantias de transação, recuperação e durabilidade de um banco relacional. Para múltiplas instâncias, usaria PostgreSQL ou SQL Server gerenciado, migrations versionadas, isolamento/locks de linha ou atualização condicional atômica, backups testados e monitoramento. Garantiria unicidade idempotente no banco, retentativas limitadas com backoff apenas para falhas transitórias, timeout e circuit breaker nas dependências externas. Não retornaria sucesso antes do commit.

O saldo consolidado aqui significa saldo contábil resultante de lançamentos ordenados por `OccurredAt` (UTC). Requisitos de data-valor, lançamentos retroativos, ordenação total, estornos, limites/regulação e saldo disponível versus contábil precisam ser definidos pelo produto. Para histórico com alta taxa de consulta, snapshots devem ser reconstruíveis e conciliados com o ledger.

Antes de exposição externa, acrescentaria autenticação/autorização por conta, TLS, gestão de segredos, criptografia e política de retenção, minimização de dados pessoais, auditoria de acesso, rate limiting, limites de payload, métricas/traces/logs sem dados sensíveis, health checks, migrations no deploy, backup/restore e reconciliação operacional. `OwnerReference` deve ser um identificador opaco; não envie CPF ou outros dados pessoais nessa propriedade.

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
