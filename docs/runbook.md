# Operação e recuperação

Este runbook cobre o ambiente demonstrativo e registra os procedimentos que precisam existir antes de uma implantação real. SLA, RTO e RPO são as premissas de `requirements-and-decisions.md`; não foram medidos nem aprovados pelo negócio.

## Inicialização local

1. Inicie os bancos independentes: `docker compose up -d financial-write-db financial-read-db`.
2. Inicie `scripts/Start-FinancialApi.ps1` para o modo `write` e `scripts/Start-FinancialReadApi.ps1` para `read`. Em `Development`, cada API aplica as migrations do seu banco.
3. Inicie `scripts/Start-FinancialProjector.ps1`. Ele publica heartbeat, backlog e eventos processados.
4. Consulte `/health/live` e `/health/ready` em `http://localhost:5080` e `http://localhost:5081`.
5. Gere um JWT local pelo script `New-LocalJwt.ps1`; o token de exemplo não é segredo e não pode ser reutilizado fora do ambiente local.

O `docker-compose.yml` guarda dados em volumes locais sem replicação. Para reiniciar um ambiente de teste limpo, pare os containers e remova apenas os volumes deste compose depois de confirmar que não há dados necessários. Não use os comandos de limpeza local em ambiente compartilhado.

## Deploy e rollback

1. Verifique build, testes automatizados, migrations pendentes e compatibilidade da versão nova com o schema atual.
2. Faça backup/snapshot e confirme que o banco está saudável.
3. Aplique migrations como etapa controlada antes de ativar os novos processos. Em produção, as APIs não aplicam migrations automaticamente. Configure `DOTNET_ENVIRONMENT=Production`, `ConnectionStrings__FinancialWrite` e `ConnectionStrings__FinancialRead` por um gerenciador de segredos e execute `dotnet run --project src/Financial.DatabaseMigrator -- all`. O comando exige TLS com `SslMode=VerifyFull`; use `write` ou `read` para migrar apenas um banco.
4. Implante primeiro o projetor compatível com eventos atuais e próximos; depois leitura e escrita. Use endpoints de readiness antes de direcionar tráfego.
5. Acompanhe erros, latência, backlog e idade da projeção durante a liberação gradual.
6. Para rollback de aplicação, reimplante a versão anterior apenas se ela for compatível com as migrations aditivas já aplicadas. Não reverta schema automaticamente. Se for necessária restauração de banco, pare as gravações e siga o procedimento aprovado de recuperação, avaliando perda de dados desde o backup.

Não há pipeline de deploy nesta entrega; o ambiente e a estratégia de rollout de produção precisam ser definidos pela empresa.

## Backup e restore

Em produção, use backup gerenciado/PITR com criptografia, controle de acesso, retenção aprovada e cópia fora da zona de falha. Proteja separadamente os bancos de escrita e leitura. A projeção é reconstruível a partir da outbox retida; o ledger de escrita é a fonte autoritativa.

Para uma cópia manual do banco local, o cliente `pg_dump` pode gravar o dump dentro do container e depois copiá-lo para fora:

```powershell
$container = docker compose ps -q financial-write-db
docker exec $container pg_dump -U financial -d financial -Fc -f /tmp/financial.dump
docker cp "${container}:/tmp/financial.dump" .\financial.dump
```

Essa cópia é apenas um exemplo para desenvolvimento; não oferece PITR, gestão de chaves nem cópia fora do host.

Restore deve ser ensaiado em banco descartável, nunca sobre o banco ativo sem aprovação do procedimento operacional. Exemplo de restauração em um banco previamente criado para o ensaio:

```powershell
$container = docker compose ps -q financial-write-db
docker cp .\financial.dump "${container}:/tmp/financial.dump"
docker exec $container pg_restore -U financial -d financial --clean --if-exists --no-owner /tmp/financial.dump
```

`pg_restore --clean` remove objetos existentes. Confirme que o destino é o banco descartável correto antes de executar.

Após restore, valide migrations, contagem de contas/lançamentos, constraints, saldos reconciliados contra a soma dos lançamentos e a possibilidade de registrar uma movimentação de teste. Depois, restaure/reconstrua a projeção e confirme `dataAsOf` e backlog. Registre duração e resultado para comparar com RTO/RPO propostos.

### Ensaio manual de backup e restore

Em 08/11/2026, foi criado um dump custom-format do banco de escrita `financial` no container `financial-write-db` e restaurado em um banco descartável separado, no mesmo container. A validação encontrou as mesmas cinco tabelas públicas e duas migrations; as contagens de contas, lançamentos, outbox e auditoria foram iguais (`0` em ambas as bases). O banco descartável e o dump temporário foram removidos após a comparação. Os dois bancos permaneceram `healthy` e as APIs de escrita e leitura responderam `200` em readiness.

Este ensaio confirma a execução básica de `pg_dump`/`pg_restore` para um banco vazio. Não valida restauração de lançamentos reais, reconciliação de saldos, recuperação da projeção, duração/RTO, PITR, criptografia ou cópia fora do host. Um ensaio com dados representativos e em infraestrutura isolada continua necessário antes de atribuir garantias operacionais ao backup.

### Reconstruir a projeção de leitura local

Pare manualmente o `Financial.Projector.Worker`. O procedimento limpa as tabelas de projeção e reaplica todos os eventos da outbox; as tabelas de auditoria permanecem intactas. No PowerShell, revise o destino e confirme a operação:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\scripts\Rebuild-FinancialReadModel.ps1
```

O script pede confirmação. Depois da reconstrução, inicie a API de leitura para confirmar migrations e então o projetor. Essa operação deixa as consultas sem projeção durante o replay; faça-a somente em janela controlada. Em produção, a reconstrução deve usar um novo datastore/projeção e troca controlada, não apagar a réplica de leitura em serviço.

## Health, sinais e alertas

- `GET /health/live`: processo ativo; não verifica dependências.
- `GET /health/ready`: banco de dados acessível. A API de leitura também retorna o estado do heartbeat, backlog e dead letters do projetor. A degradação de projeção não remove a API de leitura do balanceador, pois o último saldo projetado continua disponível.
- `X-Correlation-ID` e spans HTTP/caso de uso correlacionam uma solicitação. Não use valor de token, subject, identificador de conta ou conteúdo financeiro como label de métrica.
- Configure `OTEL_EXPORTER_OTLP_ENDPOINT` para enviar métricas e traces a um collector/backend. A aplicação não inclui armazenamento de telemetria ou dashboard.

Alertas iniciais a configurar no backend:

| Condição | Ação operacional |
| --- | --- |
| API ou PostgreSQL readiness unhealthy | Verificar processo, rede, pool e saúde do banco; manter escrita fechada se não houver commit confiável. |
| Idade do evento pendente > 30 s | Investigar worker, conexões aos dois bancos e ritmo de processamento. |
| Idade do evento pendente > 5 min | Declarar leitura degradada, acompanhar `dataAsOf` e escalar incidente. |
| Dead letter > 0 | Corrigir causa, revisar evento com acesso controlado e reprocessar por procedimento auditado. |
| Erros/latência acima dos SLOs assumidos | Correlacionar métricas, traces e logs; comparar volume/carga com a premissa validada. |
| Falha de backup ou ensaio de restore | Tratar como incidente de proteção de dados; suspender a alegação de RPO/RTO até recuperar evidência. |

Os nomes das métricas próprias estão listados em `architecture.md`. A tradução para nomes Prometheus/Datadog depende do collector e do backend escolhidos.

### Exercício manual de indisponibilidade da escrita

Em 08/11/2026, no ambiente local de desenvolvimento, foi criada uma conta de ensaio, registrado um crédito de 1.250 centavos e aguardada sua projeção. O container PostgreSQL de escrita foi parado enquanto a API de leitura e o banco de leitura permaneceram ativos. `GET /customers/me/balance` continuou retornando os 1.250 centavos projetados. O container de escrita foi reiniciado, voltou a `healthy`, e os registros da conta de ensaio foram removidos dos bancos. A verificação final confirmou os dois bancos `healthy`, as APIs com readiness `200` e nenhuma conta ou lançamento restante.

Este exercício comprova a continuidade da leitura durante a parada controlada do container de escrita neste host. Foi uma execução manual, não um teste automatizado de CI; não mede failover, RTO/RPO, recuperação de desastre ou indisponibilidade física do servidor hospedeiro.

Em 08/11/2026, a suíte `PostgresFinancialRepositoryTests` também foi executada contra o banco dedicado `financial_write_loadtest`, criando schemas temporários e removendo-os ao final. Os cinco testes passaram, incluindo a leitura consolidada após o encerramento da API de escrita, leitura sem conexão de escrita, replay idempotente da projeção, concorrência entre débitos e retries idempotentes. Isso automatiza parte da evidência de isolamento lógico e consistência, mas não simula queda do servidor PostgreSQL, múltiplas instâncias de API ou failover de infraestrutura.

### Sondagem manual de latência da consulta

Em 08/11/2026, a API de leitura local foi chamada 100 vezes em `GET /customers/me/balance`, com JWT de desenvolvimento, concorrência máxima de 10 e uma conta sintética de saldo zero na projeção. Todas as respostas foram `200`; latências observadas: p50 11,79 ms, p95 54,45 ms, p99 60,89 ms e máximo 61,92 ms. A conta e as 100 linhas de auditoria do ensaio foram removidas; a verificação final encontrou zero registros de ensaio.

Esta sondagem indica que a rota respondeu abaixo da premissa p95 de 500 ms neste ambiente e nesta amostra. Não é um teste sustentado, não representa volume ou crescimento do ledger, não mede 100 operações/s nem o pico proposto e não deve ser tratado como SLO validado. Repetir com conjunto representativo, gerador dedicado, duração definida e métricas do banco antes de concluir capacidade.

Em seguida, em 08/11/2026, foi executado um ensaio sustentado de 60 segundos com 1.000 contas sintéticas na projeção de leitura, distribuídas entre quatro titulares (250 contas por titular). Foram feitas 900 consultas autenticadas a `GET /customers/me/balance`, em cadência de aproximadamente 15 requisições/s e concorrência máxima de 4. Todas retornaram `200`; latências observadas: p50 19,19 ms, p95 33,70 ms, p99 44,13 ms e média 20,57 ms. As 1.000 contas e 900 registros de auditoria foram removidos; a verificação final encontrou zero registros do ensaio.

O p95 ficou abaixo da premissa de 500 ms nesta carga e neste conjunto. A configuração atual permite 1.000 requisições/minuto por IP de cliente e 300/minuto por titular; por isso, o cenário proposto de 100 operações/s não pode ser validado por um único gerador/origem com a configuração atual. O ensaio não representa 100 operações/s, o pico de 500 operações/s, a carga de escrita, duração longa, ou crescimento de lançamentos. Antes de usar essas metas como critério de aceite, validar a política de rate limit com o negócio e executar um teste distribuído/provisionado que não mascare saturação com rejeições `429`.

Para permitir um ensaio isolado de 100 consultas/s sem alterar os padrões normais, os limites agora podem ser sobrescritos por configuração (`RateLimits:Global`, `RateLimits:Read`, `RateLimits:Write`). Em 08/11/2026, a API de leitura foi iniciada em instância separada, na porta local `5083`, apontando para `financial_read_loadtest`; somente nela foram configurados limites de 10.000 tokens por minuto para o teto global e leitura. Com 1.000 contas sintéticas (250 para o titular consultado), foram feitas 6.000 chamadas a `GET /customers/me/balance` em 59,24 s: 101,28 chamadas/s, todas `200`, p50 17,93 ms, p95 30,49 ms, p99 65,34 ms e máximo 149,04 ms. O ensaio deixou zero registros de auditoria após limpeza; as contas sintéticas permanecem no banco dedicado. A instância temporária foi encerrada e os limites padrão das APIs normais não foram alterados.

Este resultado demonstra a rota de consulta e a projeção sintética neste host para aproximadamente 100 chamadas/s por um minuto. Não valida escrita sob carga, topologia distribuída, saturação prolongada ou disponibilidade em múltiplos hosts. Os limites são locais por processo e precisam ser calibrados e complementados no gateway antes de produção.

Em 08/11/2026, foi feito o ensaio de pico proposto por cinco minutos na mesma instância e base isoladas, agora com limites elevados somente no processo de teste para 200.000 tokens/minuto nos limiters global e de leitura. Foram concluídas 150.000 consultas a `GET /customers/me/balance` em 300,52 s (499,14 chamadas/s); todas retornaram `200`, sem `429`. Latências: p50 21,25 ms, p95 35,38 ms, p99 85,91 ms, média 24,21 ms e máximo 857,76 ms. Nos pontos de amostragem, o container PostgreSQL variou de 62% a 71% de CPU e de 140 MiB a 167 MiB de memória; `blks_read` permaneceu em 13. A auditoria do ensaio foi apagada, as 1.000 contas de fixture permaneceram, e as APIs normais e os dois bancos continuaram saudáveis.

O endpoint de prontidão da instância de ensaio marcou a projeção como `Degraded` porque nenhum projetor foi conectado à base de carga; o status HTTP permaneceu `200` e as consultas de saldo foram atendidas. Assim, o ensaio mede o caminho HTTP e a consulta agregada sobre a réplica já populada, mas não o pipeline completo de escrita/outbox/projeção. O resultado é evidência local para a leitura em torno de 500 chamadas/s, não garantia de capacidade de produção; o máximo isolado de 857,76 ms também deve ser investigado em repetição com tracing e medição do lado do banco.

### Carga de escrita e projeção ponta a ponta

Em 08/11/2026, a API de escrita isolada registrou 6.000 créditos de um centavo em `financial_write_loadtest`, distribuídos por 10 contas; o projetor independente consumiu a outbox e a API de leitura serviu `financial_read_pipeline_test`. Os limiters da instância de ensaio foram elevados por configuração, sem alterar os padrões das APIs normais. Todas as operações de escrita retornaram `201`: 102,48/s durante 58,55 s, p50 28,32 ms, p95 52,99 ms, p99 154,57 ms e máximo 466,91 ms.

O saldo agregado esperado e observado após a projeção foi 93.500 centavos para as 10 contas, reconciliado com 6.020 lançamentos no total. A outbox manteve 6.030 eventos (incluindo a carga inicial) e chegou a zero pendentes; durante a carga foram observados 1.801 pendentes aos 30 s e 1.810 ao fim, depois o backlog caiu para 710 e finalmente zero. O tempo exato de drenagem não foi instrumentado. Readiness das duas APIs isoladas ficou `Healthy` com o worker ativo. As APIs de teste e o worker foram encerrados; os bancos dedicados e os lançamentos sintéticos permanecem para repetição, sem dados na base financeira ativa.

Este ensaio mostra o caminho transacional e a projeção eventual a aproximadamente 100 escritas/s neste host e com 10 contas. Não comprova o pico de 500 escritas/s, múltiplas instâncias, falha de processo durante commit, nem capacidade sob crescimento prolongado ou infraestrutura distribuída. O aumento temporário dos limiters serviu apenas para o ensaio isolado.

Em 08/11/2026, foi executado um ensaio isolado de cinco minutos a aproximadamente 500 escritas/s, totalizando 150.000 operações. Todas retornaram `201`; p50 43,93 ms, p95 317,13 ms, p99 701,64 ms e máximo 1.027,72 ms. O p95 excedeu a referência provisória de 300 ms usada no ensaio. Ao final, a outbox ainda tinha backlog, portanto o resultado da API não representa capacidade sustentada do pipeline completo.

Com o worker otimizado para enviar comandos em lote e marcar eventos processados em uma atualização, a fila de `financial_write_loadtest` foi drenada. Uma janela de 30 s observou 10.200 eventos processados (aproximadamente 340/s), seguida de zero pendentes. A reconciliação final encontrou 50 contas, 176.471 lançamentos e saldo agregado de 263.951 centavos tanto na escrita quanto na leitura. O tempo total exato desde o backlog de 22.500 até zero não foi cronometrado. Esta é evidência local de drenagem e consistência eventual; não comprova failover, isolamento entre instâncias, capacidade em outra infraestrutura ou um SLA de produção.

### Base persistente de dados sintéticos para consulta

Foi criado o banco dedicado `financial_read_loadtest` no container `financial-read-db`, separado do banco `financial_read` usado pela API ativa. Ele contém 1.000 contas sintéticas de leitura, distribuídas igualmente por `loadtest-customer-001` a `loadtest-customer-004` (250 contas por titular). A base foi verificada após duas execuções do seed: continua com 1.000 contas, enquanto o banco ativo permanece sem essas contas.

Para recriar ou atualizar os fixtures, execute no PowerShell com acesso SSH ao servidor:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\scripts\Seed-FinancialReadLoadTest.ps1
```

O script cria a base e copia apenas o schema de leitura quando ela ainda não existe; os dados são idempotentes e mantidos no banco dedicado. O SQL recusa execução em qualquer banco cujo nome não seja `financial_read_loadtest`. Os fixtures populam somente a projeção de contas, não o ledger, entries ou outbox; servem para exercitar agregação e latência de leitura, não consistência financeira ponta a ponta. Para um teste pela API, configure uma instância isolada com `ConnectionStrings__FinancialRead` apontando para `financial_read_loadtest`; não repontar a API compartilhada durante uso normal.

### Restore com ledger sintético não vazio

Em 08/11/2026, foi criado o banco isolado `financial_write_loadtest` e a API de escrita temporária foi iniciada apenas em `localhost:5082`. Pela API foram registradas 10 contas, cada uma com crédito de 10.000 centavos e débito de 1.250 centavos. O banco manteve 20 lançamentos, 30 eventos de outbox, 30 eventos de auditoria e duas migrations; o total dos saldos (87.500 centavos) conferiu com a soma dos valores assinados dos lançamentos. As três constraints verificadas para valor assinado, idempotência e instante por conta estavam presentes.

O banco foi exportado com `pg_dump` e restaurado em uma base descartável no mesmo container. Contagens, constraints e reconciliação de saldo foram iguais entre origem e restauração. A base de restauração e o dump temporário foram removidos; `financial_write_loadtest` permanece para ensaios futuros, e a API temporária foi encerrada. Este ensaio valida restore lógico de um ledger sintético não vazio; não mede duração de RTO, PITR, cópia fora do host nem recuperação de infraestrutura independente.

## Troubleshooting

### Escrita retorna 503 ou fica lenta

Consulte `/health/ready` na API de escrita. Verifique conectividade e failover do banco de escrita, credenciais, pool de conexões e logs correlacionados. Repetições do cliente devem reutilizar a mesma `idempotencyKey`; não confirme operação sem resposta de sucesso.

### Saldo de leitura ainda não mostra um lançamento

Consulte `dataAsOf` e `/health/ready` na API de leitura. Verifique se o worker está ativo, se ambas as conexões estão configuradas e se existe backlog antigo. Não leia diretamente do banco de escrita como fallback silencioso, pois isso mudaria a consistência e a dependência de disponibilidade da API de consulta.

### Evento foi para dead letter

Use acesso restrito para identificar `event_id`, tipo e tentativas. Preserve o evento original, corrija a causa (por exemplo, incompatibilidade de schema), valide a correção em ambiente de teste e só então limpe o estado dead letter por procedimento versionado e auditado. Não altere o payload financeiro para fazê-lo passar.

### Saldo inconsistente

Interrompa novas movimentações da conta afetada conforme procedimento do banco, preserve logs e dados, reconcilie lançamento a lançamento e gere ajuste compensatório aprovado. Nunca edite ou apague lançamentos confirmados para “corrigir” o saldo.

## Limitações do exercício

Não existe automação de backup, restore, failover de PostgreSQL, alertas provisionados, deploy gradual ou reconciliação de negócio. O runbook define o procedimento esperado, mas a equipe precisa executá-lo e guardar evidências antes de uma operação real.
