# Arquitetura da revisão

As premissas funcionais, decisões de domínio e metas provisórias estão em [`requirements-and-decisions.md`](requirements-and-decisions.md). Esta documentação descreve o desenho implementado e distingue o ambiente demonstrativo das garantias que exigem infraestrutura gerenciada.

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

O mesmo projeto `Financial.Api` pode iniciar nos modos `write` ou `read`, configurados por `Financial:ApiRole`, e ser executado como processos e implantações separados. O modo `combined` existe apenas para testes e desenvolvimento. O projetor é um processo independente. Os bancos de leitura e escrita usam conexões e schemas próprios.

O domínio contém `Account`, `Money` e `FinancialEntry`. A aplicação define as portas de escrita e consulta. `Financial.Infrastructure` implementa PostgreSQL, migrations, outbox, projeção e um adapter JSON restrito a testes. A API faz autenticação, autorização e tradução HTTP; o worker aplica eventos à projeção.

## Escrita atômica e concorrência

Uma gravação executa estas etapas dentro de uma transação PostgreSQL:

1. Bloqueia a linha da conta com `SELECT ... FOR UPDATE`.
2. Confere titular, chave idempotente, fundos e overflow.
3. Atualiza saldo, insere lançamento, evento de auditoria e evento da outbox.
4. Confirma tudo junto. Uma falha em qualquer etapa reverte todas as alterações.

A constraint única `(account_id, idempotency_key)` é a última barreira contra duplicação e funciona entre processos. O lock por conta serializa débitos concorrentes para a mesma conta sem bloquear movimentações em contas diferentes. A aplicação não confirma sucesso antes do commit.

O consumidor pode receber/reprocessar o mesmo evento. `financial_projected_events` torna a aplicação idempotente; lançamento e marcador do evento são confirmados juntos no banco de leitura. Depois, o worker marca a outbox como processada no banco de escrita. Se cair entre esses commits, o evento é repetido e ignorado com segurança. Um advisory lock limita o projetor a uma execução ativa por banco de escrita.

## Leitura e consistência

A API de leitura consulta apenas o banco projetado, que mantém contas, saldos atuais, movimentos históricos, auditoria de consulta e estado do projetor. A consulta do saldo atual usa o saldo materializado; a consulta histórica soma lançamentos indexados até `at`. O consolidado é calculado por titular e moeda assumida.

A consistência entre escrita e leitura é eventual. `dataAsOf` informa o instante mais recente aplicado à conta/projeção. O cliente deve tratar esse campo como atualidade da resposta e pode repetir a consulta após uma gravação. Com o projetor parado, a API de leitura continua atendendo a última projeção. O estado de health informa heartbeat, backlog e idade do evento pendente.

O alvo proposto é aplicar eventos em até 30 segundos em condições normais e sinalizar degradação quando o evento pendente mais antigo passar de 5 minutos. Em ensaio local sustentado por cinco minutos, a fila chegou a zero até cinco segundos após o fim da carga, mas o atraso máximo por evento não foi instrumentado. Portanto, o resultado não comprova o limite de 30 segundos. Esses valores são premissas de avaliação e precisam de validação pelo negócio.

## Persistência e migrations

`001_InitialSchema.sql` cria ledger e outbox, `002_ReadProjection.sql` cria a projeção, e `003_AuditAndDeadLetters.sql` adiciona auditoria financeira e dead letters. `PostgresMigrationRunner` registra versões e usa advisory lock para impedir aplicação simultânea. Em `Development`, cada API aplica as migrations do respectivo banco ao iniciar. Em produção, migrations devem rodar como etapa controlada do deploy, antes de liberar a nova versão; as APIs não alteram schema automaticamente.

As migrations são aditivas e versionadas. Rollback de aplicação não apaga dados nem executa uma migration destrutiva reversa. A reversão de schema exige procedimento próprio após avaliação de compatibilidade.

## Falhas e recuperação

| Falha | Comportamento |
| --- | --- |
| API de escrita parada | Leituras continuam no banco projetado. Novos lançamentos não são confirmados. |
| Banco de escrita indisponível | Novos lançamentos falham; a API de leitura mantém a última projeção independente. |
| Worker ou banco de leitura indisponível | Lançamentos confirmados ficam na outbox; leituras existentes continuam quando o banco de leitura está acessível e mostram `dataAsOf`. |
| Queda após commit de leitura e antes da confirmação da outbox | O evento reaparece; o ID já projetado impede duplicação. |
| Evento inválido permanente | Após 10 tentativas, fica marcado como dead letter; o worker continua com os próximos eventos e emite log/telemetria. Reprocessamento exige correção operacional. |
| Banco de leitura perdido | É reconstruível pela outbox enquanto os eventos forem retidos; há script manual para o ambiente local, sem troca sem indisponibilidade. |
| Banco de escrita perdido | O protótipo falha novas escritas e exige restore; o failover síncrono multi-zona é uma proposta descrita em [`ADR-0001`](decisions/adr-0001-postgresql-high-availability.md), ainda não provisionada. |

O banco de leitura ainda depende de estar disponível para responder consultas; separar bancos e processos não é failover. O Docker Compose local usa duas instâncias PostgreSQL independentes, mas não fornece replicação, failover, redundância de zona ou backup externo. Portanto, a topologia local demonstra isolamento de processo e datastore, mas não prova disponibilidade mensal nem RPO/RTO de produção.

A decisão de alta disponibilidade especifica primário/standby síncrono, fencing, endpoint estável, recuperação da antiga primária e critérios de failover. Como o host de ensaio não oferece outra zona ou um serviço gerenciado, estes itens são desenho-alvo e não comportamento implementado. Consulte [`ADR-0001`](decisions/adr-0001-postgresql-high-availability.md) antes de selecionar/provisionar a plataforma.

## Segurança

- Os modos `write` e `read` exigem `financial.write` e `financial.read`, respectivamente. São aceitos claims `scope` e `scp` com scopes separados por espaço.
- Fora de `Development`, a API exige `Jwt:Authority`, valida issuer/audience/expiração e recusa `Jwt:SigningKey`. A identidade do titular vem de `sub` e é comparada sem normalização.
- O limiter aplica teto global por IP e limites específicos por subject; os valores são configuráveis em `RateLimits:Global`, `RateLimits:Read` e `RateLimits:Write` (token limit, tokens por período e duração em segundos). Os padrões permanecem em 1.000/minuto por IP, 300/minuto por titular em leitura e 60/minuto por titular em escrita. O corpo HTTP é limitado a 64 KiB e cabeçalhos a 16 KiB. Os contadores são locais ao processo, portanto uma implantação com várias instâncias precisa complementar isso com limites no gateway/WAF ou um limiter distribuído.
- Auditoria financeira registra ator, ação, conta, transação e instante dentro da transação de escrita. O banco de leitura registra consultas sem guardar valores financeiros.
- Logs e métricas não incluem subject, conta, lançamento, idempotency key, descrição ou valor. O middleware devolve `X-Correlation-ID`; ASP.NET e o serviço de domínio geram spans correlacionados. O log de conclusão de cada requisição fica em `Debug` para evitar volume excessivo em carga; os logs informativos do framework ASP.NET ficam em `Warning`.
- O caminho de escrita publica spans para o lock da conta, lote de comandos PostgreSQL e commit, além de histogramas de duração e tamanho do lote com resultado `success`, `not_found` ou `error`. Esses sinais permitem separar espera pelo lock, execução das gravações e sincronização do commit durante ensaios de carga; não carregam identificadores financeiros.
- Em ambiente real, segredo JWT fica no provedor de identidade; credenciais de banco devem vir de secret manager. A conexão PostgreSQL exige `SslMode=VerifyFull` fora de Development. Criptografia em repouso e retenção dependem da plataforma e da política aprovada.

## Operação e observabilidade

- `/health/live` verifica que o processo responde; `/health/ready` verifica banco e, para leitura, heartbeat/backlog do projetor. Projeção degradada informa status sem remover a API de leitura do balanceador, pois a última posição continua disponível.
- Logs são JSON estruturados com correlação. OpenTelemetry coleta instrumentação ASP.NET/HTTP, traces do caso de uso e métricas de transações e projeção. Configure `OTEL_EXPORTER_OTLP_ENDPOINT` e um backend para armazenar, consultar e alertar sobre os sinais.
- O Compose oferece o perfil opcional `observability` com Aspire Dashboard standalone para inspecionar traces, métricas e logs em desenvolvimento. A ingestão OTLP e a API de leitura do dashboard exigem chaves separadas; as portas ficam em loopback por padrão. O dashboard não substitui retenção, alertas ou um backend de produção.
- Métricas próprias incluem lançamentos aceitos, repetições idempotentes, eventos projetados, duração de lote, eventos pendentes, dead letters e idade do backlog.
- O script [`Reconcile-FinancialProjection.sh`](../scripts/Reconcile-FinancialProjection.sh) verifica saldos internos, cobertura de eventos maduros, backlog e dead letters entre escrita e projeção. Ele pode ser agendado e expõe métricas ao Prometheus; não substitui reconciliação de negócio ou comparação com extratos externos.
- Procedimentos de deploy, backup/restore, rollback e troubleshooting estão em [`runbook.md`](runbook.md). O backup local do exercício não é recuperação de desastre.

## Limites restantes

Em uma execução sustentada anterior, p95 e p99 excederam as referências provisórias. Após instrumentar lock, lote e commit, uma repetição de cinco minutos a aproximadamente 500 escritas/s atingiu p95 de 155,25 ms e p99 de 600,02 ms, com zero backlog ao final e reconciliação exata da projeção. O resultado comprova essas metas apenas para um ensaio neste host e conjunto de contas; os maiores p95 por janela apontam picos de commit/lock que ainda precisam de correlação em tracing. Os resultados e limites estão em [`runbook.md`](runbook.md); não representam SLO aprovado nem capacidade em outra infraestrutura. Ainda faltam replicação/failover de PostgreSQL, plataforma multi-zona, proxy/balanceador, rotação real de secrets, política legal de retenção, reconciliação financeira com extratos externos e teste de failover multi-host. O plano-alvo está em [`ADR-0001`](decisions/adr-0001-postgresql-high-availability.md). A outbox mantém eventos processados e pode crescer indefinidamente até uma política de arquivamento ser aprovada. As premissas numéricas devem ser ajustadas antes de tratar esta arquitetura como compromisso para produção.
