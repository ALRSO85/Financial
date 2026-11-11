# Arquitetura

Este documento descreve o que está implementado no sandbox e separa isso da arquitetura de produção proposta. As premissas de domínio e as metas de ensaio estão em [`requirements-and-decisions.md`](requirements-and-decisions.md); os detalhes de alta disponibilidade estão na [ADR-0001](decisions/adr-0001-postgresql-high-availability.md).

## Componentes

```text
Cliente ──JWT──► Financial.Api (write) ──► PostgreSQL de escrita
                         │                     ├── ledger/contas
                         │                     ├── auditoria
                         │                     └── outbox transacional
                         │                              │
                         │                    Financial.Projector.Worker
                         │                              │
Cliente ──JWT──► Financial.Api (read) ◄── PostgreSQL de leitura/projeção
```

O projeto `Financial.Api` pode iniciar em modo `write` ou `read` e ser executado em processos separados. O modo `combined` fica restrito a testes e desenvolvimento. O projetor também roda como processo independente, e os bancos de leitura e escrita usam conexões próprias.

O domínio contém `Account`, `Money` e `FinancialEntry`. A camada de aplicação define as operações; a infraestrutura implementa PostgreSQL, migrations, outbox e projeção. O adapter JSON é mantido para testes. A API cuida de autenticação, autorização e HTTP, e o worker atualiza o modelo de leitura.

## Escrita e idempotência

Cada gravação acontece em uma transação PostgreSQL. A API bloqueia a linha da conta com `SELECT ... FOR UPDATE`, valida o titular, a chave idempotente e o saldo, e então atualiza o saldo e grava o lançamento, a auditoria e o evento da outbox. Qualquer erro antes do commit reverte todas essas mudanças. A constraint única `(account_id, idempotency_key)` protege contra repetição inclusive quando há mais de um processo. Contas diferentes podem ser movimentadas em paralelo.

O projetor pode receber o mesmo evento mais de uma vez. `financial_projected_events` registra o que já foi aplicado; o lançamento na projeção e esse marcador são confirmados juntos. Depois, o worker marca o evento original como processado. Se cair entre as duas etapas, o evento será repetido e ignorado sem duplicar saldo. Um advisory lock impede que dois projetores consumam a mesma outbox ao mesmo tempo.

## Consulta e consistência

A API de leitura consulta apenas o banco de projeção. O saldo atual usa o valor materializado; consultas históricas somam os lançamentos até o instante informado. O consolidado soma as contas do titular segundo a premissa de moeda do exercício.

A projeção é eventualmente consistente. `dataAsOf` informa até quando os dados foram aplicados. Com o projetor parado, a API pode continuar servindo a última posição e sinalizar atraso pelo health check, que acompanha heartbeat e backlog. O alvo proposto é atualizar a projeção em até 30 segundos e sinalizar degradação quando houver evento pendente por mais de cinco minutos. Esse atraso não foi medido de ponta a ponta em carga; os valores ainda precisam ser validados pelo negócio.

## Banco e migrations

As migrations versionadas criam o ledger, a outbox, a projeção, a auditoria e as dead letters. `PostgresMigrationRunner` registra as versões aplicadas e usa advisory lock para evitar execução concorrente. Em `Development`, cada API pode aplicar as migrations do seu banco ao iniciar. Em produção, o `Financial.DatabaseMigrator` deve rodá-las como etapa controlada do deploy, antes de liberar a nova versão.

As migrations são aditivas. O rollback da aplicação não apaga dados nem reverte o schema automaticamente; uma reversão exige avaliar compatibilidade e definir um procedimento próprio.

## Falhas conhecidas

| Falha | Comportamento observado ou esperado |
| --- | --- |
| API de escrita parada | A consulta segue atendendo a projeção. Novas movimentações não são confirmadas. |
| Banco de escrita indisponível | A escrita falha sem confirmar lançamentos; a leitura pode servir a última projeção independente. |
| Projetor ou banco de leitura indisponível | A outbox retém eventos confirmados. A leitura pode ficar indisponível ou desatualizada enquanto o banco de leitura não responde. |
| Queda após gravar a projeção, antes de confirmar a outbox | O evento é repetido e o identificador projetado evita duplicação. |
| Evento inválido persistente | Após dez tentativas, o evento vai para dead letter. O worker continua com os seguintes e registra telemetria; a correção e o reprocessamento exigem procedimento operacional. |
| Perda do banco de leitura | A projeção pode ser refeita da outbox enquanto os eventos necessários forem mantidos. O script de reconstrução é manual e não faz troca sem indisponibilidade. |
| Perda do banco de escrita | Novas gravações param até a recuperação. Failover síncrono em múltiplas zonas está descrito na [ADR-0001](decisions/adr-0001-postgresql-high-availability.md), mas não foi provisionado. |

Os dois bancos do Compose são instâncias separadas, mas ficam no mesmo host e não têm replicação ou failover. Essa configuração permite testar a separação dos processos e dos dados; não demonstra disponibilidade mensal, RPO ou RTO de produção. O banco de leitura também continua sendo um ponto único de falha para consultas.

## Segurança e operação

Os modos `write` e `read` exigem, respectivamente, os escopos `financial.write` e `financial.read`. Fora de `Development`, a API valida issuer, audience e expiração a partir de um provedor configurado e recusa a chave simétrica local. O titular vem do claim `sub`. Os limiters têm um teto global por IP e limites por titular; como os contadores são locais ao processo, múltiplas instâncias precisam de um gateway, WAF ou limitador distribuído. O corpo HTTP é limitado a 64 KiB e os cabeçalhos a 16 KiB.

As operações financeiras e consultas geram auditoria. Logs e métricas não carregam subject, conta, lançamento, chave idempotente, descrição ou valor. As requisições recebem `X-Correlation-ID`; traces separam espera pelo lock, gravações e commit. Credenciais de runtime não têm DDL nem `DELETE`, enquanto o migrador usa credenciais próprias. No sandbox, essa separação foi aplicada aos bancos de monitoramento.

`/health/live` verifica o processo. `/health/ready` acompanha banco e, na API de leitura, a atualidade da projeção. OpenTelemetry pode enviar traces e métricas ao endpoint configurado. O Compose inclui Prometheus, Blackbox Exporter e Alertmanager para ensaios, mas não há receiver externo nem retenção operacional de produção. O runbook cobre deploy, backup/restore, rollback e troubleshooting; o backup local não protege contra perda do host.

## Limites do sandbox

O código atual não provisiona standby, failover, proxy de conexão, implantação multi-zona, PITR, secret manager ou cópia externa de backup. O sandbox também permite explicitamente backups em claro até que sejam configurados recipient `age` e destino. Os ensaios de carga e restore locais estão registrados no [`runbook.md`](runbook.md); eles não comprovam failover físico nem as metas provisórias em outra infraestrutura.
