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
