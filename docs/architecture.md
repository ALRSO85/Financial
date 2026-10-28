# Arquitetura e garantias

## Limites da solução

A solução registra créditos e débitos em uma conta e consulta saldo atual ou histórico. Não movimenta valores entre contas nem integra com sistemas de pagamento.

```text
Financial.Api ────────────────┐
  rotas HTTP e Swagger     │
                           ▼
Financial.Infrastructure ──► Financial.Application ──► Financial.Domain
  JSON repository          casos de uso             regras e modelo
```

As dependências apontam para dentro: o domínio não conhece HTTP, JSON ou frameworks; a aplicação depende do domínio e define a porta `IFinancialRepository`; a infraestrutura implementa essa porta; a API monta o grafo e converte HTTP para casos de uso.

## DDD aplicado

- **`Account` é o agregado raiz:** controla a alteração do saldo e rejeita débitos que o deixariam negativo. A aplicação atualiza o agregado e persiste a movimentação como uma única operação lógica.
- **`Money` é um value object:** representa valores em centavos inteiros e centraliza a soma com detecção de overflow.
- **`FinancialEntry` é um registro imutável:** documenta o movimento e guarda valor e valor assinado. Correções devem ser novos lançamentos compensatórios.
- **`FinancialApplicationService` coordena casos de uso:** valida a entrada, aplica idempotência, carrega o agregado, pede a mudança ao domínio e persiste o estado.
- **`IFinancialRepository` é uma porta da aplicação:** o armazenamento atual usa JSON, mas pode ser trocado por um adapter de banco sem levar detalhes de persistência para as regras de domínio.

O desenho usa poucos conceitos para manter o exercício legível. Não adiciona eventos de domínio, barramento, CQRS ou uma hierarquia extensa de repositórios, pois o escopo não precisa deles.

A consulta `GET /customers/me/balance` consolida todas as contas associadas ao `sub` autenticado e aceita o mesmo corte histórico da consulta por conta. O modelo não armazena um código de moeda; para este desafio, assume que todas as contas são em reais (BRL). Em um domínio com múltiplas moedas, a moeda faria parte do modelo e a consolidação seria apresentada por moeda, sem somar valores diferentes sem uma regra de conversão explícita.

## Fluxo de lançamento

1. A API valida o formato da requisição e chama o caso de uso.
2. A aplicação valida campos e procura a chave idempotente na conta. Uma repetição equivalente devolve o resultado anterior; mesma chave com conteúdo diferente retorna 409.
3. O agregado `Account` aplica o crédito/débito, valida valor positivo, saldo suficiente e overflow.
4. A aplicação adiciona um `FinancialEntry` imutável e salva o snapshot atualizado.
5. A infraestrutura escreve JSON em arquivo temporário e o move sobre o arquivo ativo.

Um `SemaphoreSlim` global na aplicação serializa todas as chamadas na instância, inclusive consultas. Isso protege o acesso ao snapshot JSON e a sequência leitura-modificação-gravação enquanto há uma única instância, mas impede leituras concorrentes e limita a capacidade. Esse limite é intencional no protótipo; para produção, o armazenamento e a estratégia de concorrência precisam ser substituídos por operações transacionais e controle por conta no banco.

## Consultas e persistência

O saldo atual está materializado no agregado, mas como cada chamada carrega e desserializa o arquivo completo, a leitura atual também cresce com o tamanho do JSON. O saldo histórico soma entradas até `OccurredAt` em memória. Os instantes são serializados em ISO-8601 e mantidos monotônicos por conta: se o relógio recuar ou repetir um instante, o próximo lançamento avança um tick após o último registrado. O `AsOf` do saldo atual não fica antes do último movimento. `OccurredAt` é o horário de registro ordenado, não uma eventual data-valor do negócio. Em produção, essa ordenação deve vir de sequência/commit do banco, com relógio sincronizado para auditoria.

O parâmetro histórico `at` usa o padrão brasileiro `dd/MM/yyyy HH:mm:ss zzz` e exige o fuso. Exemplo no horário de Brasília: `28/10/2026 09:00:00 -03:00`. As respostas JSON seguem ISO 8601; a aplicação compara os instantes e persiste `OccurredAt` em UTC.

O armazenamento JSON foi escolhido porque o objetivo desta entrega é validar o desenho e os casos de uso sem exigir a instalação de um servidor ou introduzir um provider de banco. O arquivo é legível, fácil de inspecionar e os testes podem criar um arquivo isolado por execução. SQLite também seria uma opção local válida se o exercício priorizasse transações relacionais; ainda assim, adicionaria provider e comportamento específico de banco, e continuaria sendo uma base em arquivo adequada a uma única instância, não uma solução de alta disponibilidade.

Essa escolha é deliberadamente limitada ao protótipo: o arquivo completo é regravado em cada alteração; o bloqueio não coordena processos diferentes; não há índices nem transação de banco. O arquivo temporário reduz o risco de truncamento, mas não fornece as garantias de durabilidade e recuperação de uma base transacional.

O ponto de troca do armazenamento é a porta `IFinancialRepository`, definida em `Financial.Application`. `JsonFinancialRepository` é o adapter atual; a composição de dependências em `FinancialPersistenceRegistration` seleciona o provider configurado por `Financial:StorageProvider` (por padrão, `json`) e falha na inicialização para providers ainda não implementados. Para usar PostgreSQL ou SQL Server, seria necessário adicionar um adapter que implemente a mesma interface, criar migrations e restrições de unicidade para idempotência, e registrar esse adapter nesse ponto. Os casos de uso e o domínio não precisariam conhecer o provider. A implementação de banco deve persistir saldo e lançamento na mesma transação e proteger atualizações concorrentes com lock de linha ou atualização condicional.

O repositório valida as coleções, IDs únicos, chaves idempotentes por conta, valores assinados, ordem temporal, saldo não negativo e reconciliação entre saldos materializados e lançamentos ao carregar e salvar. Se o arquivo não puder ser desserializado ou violar essas invariantes, a operação falha sem substituí-lo por um estado vazio. A recuperação deve preservar o original para análise; o protótipo não tenta reparar registros financeiros automaticamente.

## Swagger e contrato HTTP

Todas as rotas de conta exigem JWT Bearer. A API valida assinatura, emissor, audiência e expiração. O `sub` do token identifica o titular: a criação associa a conta a esse sujeito e leituras/movimentações de outra pessoa retornam 404 para evitar revelar a existência da conta. O serviço não emite tokens; a autenticação de usuários e a emissão pertencem ao provedor de identidade.

O `sub` é tratado como referência opaca e preservado exatamente como emitido; a API não remove espaços nem aplica conversões de caixa, pois isso poderia vincular a conta a uma identidade diferente.

Para execução local, a API aceita uma chave simétrica e restringe o algoritmo a HS256. No ambiente com provedor de identidade, deve-se configurar `Jwt:Authority` e retirar `Jwt:SigningKey`; os dois modos são mutuamente exclusivos para evitar uma configuração que rejeite os tokens emitidos pelo provedor.

Erros de validação, regras de aplicação e recursos não encontrados seguem o formato `ProblemDetails` (`application/problem+json`), incluindo o status HTTP e um detalhe apropriado para o cliente. Exceções inesperadas seguem o middleware padrão e não expõem mensagens internas.

No ambiente `Development`, Swagger UI fica em `/swagger` e o documento OpenAPI em `/swagger/v1/swagger.json`. A especificação declara o esquema HTTP Bearer/JWT e os endpoints têm nomes, sumários, descrições e códigos de resposta. Para ambiente real, configure `Jwt:Authority`, `Jwt:Issuer` e `Jwt:Audience` para um provedor confiável; não use a chave simétrica do script local.

| Operação | Resultado |
| --- | --- |
| `POST /accounts` | Cria uma conta a partir de uma referência opaca do titular |
| `GET /accounts/{id}` | Consulta os dados da conta somente para o titular |
| `POST /accounts/{id}/transactions` | Registra `credit` ou `debit` em centavos, usando `idempotencyKey` |
| `GET /accounts/{id}/transactions/{transactionId}` | Consulta um lançamento somente para o titular da conta |
| `GET /accounts/{id}/balance` | Retorna o saldo atual |
| `GET /accounts/{id}/balance?at={data/hora}` | Retorna o saldo até o instante informado (`dd/MM/yyyy HH:mm:ss zzz`) |
| `GET /customers/me/balance` | Consolida as contas do titular autenticado, no instante atual ou em `at` |

## Evolução para produção

Fechar requisitos de moeda e escala, reservas, saldo disponível versus contábil, estornos, lançamentos retroativos, ordenação e retenção. Trocar o adapter JSON por PostgreSQL ou SQL Server, com migrations versionadas, restrição de idempotência no banco, atualização condicional/lock de linha, backups testados e reconciliação. Integrar a autenticação JWT a um provedor de identidade gerenciado e ampliar autorização com escopos/políticas conforme os perfis de acesso. Completar criptografia, gestão de segredos, auditoria e telemetria sem dados sensíveis. Definir metas de disponibilidade e latência com o negócio antes de escolher cache, particionamento ou distribuição.
