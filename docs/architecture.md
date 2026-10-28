# Arquitetura e garantias

## Limites da solução

A solução registra créditos e débitos em uma conta e consulta saldo atual ou histórico. Não movimenta valores entre contas nem integra com sistemas de pagamento.

```text
Ledger.Api ────────────────┐
  rotas HTTP e Swagger     │
                           ▼
Ledger.Infrastructure ──► Ledger.Application ──► Ledger.Domain
  JSON repository          casos de uso             regras e modelo
```

As dependências apontam para dentro: o domínio não conhece HTTP, JSON ou frameworks; a aplicação depende do domínio e define a porta `ILedgerRepository`; a infraestrutura implementa essa porta; a API monta o grafo e converte HTTP para casos de uso.

## DDD aplicado

- **`Account` é o agregado raiz:** controla a alteração do saldo e rejeita débitos que o deixariam negativo. A aplicação atualiza o agregado e persiste a movimentação como uma única operação lógica.
- **`Money` é um value object:** representa valores em centavos inteiros e centraliza a soma com detecção de overflow.
- **`LedgerEntry` é um registro imutável:** documenta o movimento e guarda valor e valor assinado. Correções devem ser novos lançamentos compensatórios.
- **`LedgerApplicationService` coordena casos de uso:** valida a entrada, aplica idempotência, carrega o agregado, pede a mudança ao domínio e persiste o estado.
- **`ILedgerRepository` é uma porta da aplicação:** o armazenamento atual usa JSON, mas pode ser trocado por um adapter de banco sem levar detalhes de persistência para as regras de domínio.

O desenho usa poucos conceitos para manter o exercício legível. Não adiciona eventos de domínio, barramento, CQRS ou uma hierarquia extensa de repositórios, pois o escopo não precisa deles.

## Fluxo de lançamento

1. A API valida o formato da requisição e chama o caso de uso.
2. A aplicação valida campos e procura a chave idempotente na conta. Uma repetição equivalente devolve o resultado anterior; mesma chave com conteúdo diferente retorna 409.
3. O agregado `Account` aplica o crédito/débito, valida valor positivo, saldo suficiente e overflow.
4. A aplicação adiciona um `LedgerEntry` imutável e salva o snapshot atualizado.
5. A infraestrutura escreve JSON em arquivo temporário e o move sobre o arquivo ativo.

Um `SemaphoreSlim` na aplicação serializa operações dentro da instância. Isso protege a sequência leitura-modificação-gravação apenas enquanto há uma única instância do processo.

## Consultas e persistência

O saldo atual está materializado no agregado, mas como cada chamada carrega e desserializa o arquivo completo, a leitura atual também cresce com o tamanho do JSON. O saldo histórico soma entradas até `OccurredAt` em memória. Os instantes são serializados em ISO-8601. `OccurredAt` é a hora de registro, não uma eventual data-valor do negócio.

O armazenamento JSON foi escolhido para simplificar a execução do exercício. O arquivo completo é regravado em cada alteração; o bloqueio não coordena processos diferentes; não há transação de banco, índices ou recuperação equivalente a um banco relacional. O arquivo temporário reduz o risco de truncamento durante a gravação, mas não oferece garantias de durabilidade de uma base transacional.

## Swagger e contrato HTTP

Todas as rotas de conta exigem JWT Bearer. A API valida assinatura, emissor, audiência e expiração. O `sub` do token identifica o titular: a criação associa a conta a esse sujeito e leituras/movimentações de outra pessoa retornam 404 para evitar revelar a existência da conta. O serviço não emite tokens; a autenticação de usuários e a emissão pertencem ao provedor de identidade.

No ambiente `Development`, Swagger UI fica em `/swagger` e o documento OpenAPI em `/swagger/v1/swagger.json`. A especificação declara o esquema HTTP Bearer/JWT e os endpoints têm nomes, sumários, descrições e códigos de resposta. Para ambiente real, configure `Jwt:Authority`, `Jwt:Issuer` e `Jwt:Audience` para um provedor confiável; não use a chave simétrica do script local.

| Operação | Resultado |
| --- | --- |
| `POST /accounts` | Cria uma conta a partir de uma referência opaca do titular |
| `POST /accounts/{id}/transactions` | Registra `credit` ou `debit` em centavos, usando `idempotencyKey` |
| `GET /accounts/{id}/balance` | Retorna o saldo atual |
| `GET /accounts/{id}/balance?at={ISO-8601}` | Retorna o saldo até o instante informado |

## Evolução para produção

Fechar requisitos de moeda e escala, reservas, saldo disponível versus contábil, estornos, lançamentos retroativos, ordenação e retenção. Trocar o adapter JSON por PostgreSQL ou SQL Server, com migrations versionadas, restrição de idempotência no banco, atualização condicional/lock de linha, backups testados e reconciliação. Acrescentar autenticação/autorização, criptografia, gestão de segredos, auditoria e telemetria sem dados sensíveis. Definir metas de disponibilidade e latência com o negócio antes de escolher cache, particionamento ou distribuição.
