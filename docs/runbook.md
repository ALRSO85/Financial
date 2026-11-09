# Operação e recuperação

Este runbook cobre o ambiente demonstrativo e registra os procedimentos que precisam existir antes de uma implantação real. SLA, RTO e RPO são as premissas de `requirements-and-decisions.md`; não foram medidos nem aprovados pelo negócio.

## Inicialização local

1. Inicie os bancos independentes: `docker compose up -d financial-write-db financial-read-db`.
2. Inicie `scripts/Start-FinancialApi.ps1` para o modo `write` e `scripts/Start-FinancialReadApi.ps1` para `read`. Em `Development`, cada API aplica as migrations do seu banco.
3. Inicie `scripts/Start-FinancialProjector.ps1`. Ele publica heartbeat, backlog e eventos processados.
4. Consulte `/health/live` e `/health/ready` em `http://localhost:5080` e `http://localhost:5081`.
5. Gere um JWT local pelo script `New-LocalJwt.ps1`; o token de exemplo não é segredo e não pode ser reutilizado fora do ambiente local.

### Diagnóstico OpenTelemetry local

O serviço `financial-observability` fica no perfil Compose `observability` e usa o Aspire Dashboard standalone. Por padrão, a interface e as portas OTLP ficam vinculadas a `127.0.0.1`. A UI exige token de navegador, a ingestão OTLP usa uma chave e a API de consulta usa outra. O dashboard é para diagnóstico de desenvolvimento e retém telemetria apenas de forma limitada; não é o armazenamento de produção nem gera alertas.

1. Copie `.env.example` para `.env` e gere duas chaves aleatórias distintas com pelo menos 32 bytes; não versione `.env`.
2. Se as APIs estiverem em outra máquina da rede privada, defina `OBSERVABILITY_BIND_ADDRESS` no `.env` com o endereço privado do host do dashboard. Mantenha o serviço acessível somente a emissores confiáveis.
3. Inicie o serviço com `docker compose --profile observability up -d financial-observability`.
4. Configure nas APIs e no worker `OTEL_EXPORTER_OTLP_ENDPOINT=http://<host-observability>:4317`, `OTEL_EXPORTER_OTLP_PROTOCOL=grpc` e `OTEL_EXPORTER_OTLP_HEADERS=x-otlp-api-key=<chave-de-ingestao>`.
5. Abra `http://<host-observability>:18888` e use o link de login emitido nos logs do container. Para confirmar ingestão pela API de telemetria, envie `x-api-key: <chave-da-api>` a `GET /api/telemetry/resources` ou `GET /api/telemetry/traces`.

As portas usam HTTP/gRPC sem TLS no Compose demonstrativo; restrinja-as à máquina local ou a uma rede privada controlada. Para produção, use um coletor/backend operado, TLS, gestão e rotação de chaves, retenção aprovada e alertas. Consulte a [documentação do Aspire Dashboard standalone](https://aspire.dev/dashboard/standalone/) e as [considerações de segurança](https://aspire.dev/dashboard/security-considerations/).

### Prometheus, sondagens e alertas

O perfil Compose `monitoring` inclui Prometheus, Blackbox Exporter e Alertmanager. Prometheus retém métricas por 15 dias no volume `financial-prometheus-data`; Alertmanager guarda estado de silêncios no volume `financial-alertmanager-data`. Os dois painéis são publicados somente em loopback por padrão (`127.0.0.1:9090` e `127.0.0.1:9093`), pois não têm autenticação nativa habilitada neste exercício. Não altere o bind para a rede privada sem proteger o acesso com firewall e proxy autenticado.

Para iniciar apenas os componentes de monitoramento no host de ensaio, sem recriar os bancos do Compose:

```powershell
docker compose -p financial-monitoring --profile monitoring up -d financial-blackbox-exporter financial-alertmanager financial-prometheus
```

As sondagens TCP para as portas publicadas do PostgreSQL (5432 e 5433) são automáticas. Elas verificam conectividade TCP, não autenticação, `pg_isready` ou consistência do banco. Se as portas estiverem publicadas em um endereço específico do host, defina `MONITORING_HOST_ADDRESS` com esse endereço no `.env`; o padrão `host-gateway` usa o gateway Docker. No servidor de ensaio, onde os bancos estão vinculados ao IP LAN `192.168.88.200`, configure `MONITORING_HOST_ADDRESS=192.168.88.200`. As sondagens HTTP das APIs apontam para os serviços do perfil `applications` na mesma rede Docker; para uma implantação diferente, edite os arquivos `monitoring/targets/api-liveness.yml` e `monitoring/targets/api-readiness.yml` com endereços alcançáveis pelo Blackbox Exporter.

```yaml
- targets: [http://financial-write-api:5080/health/live]
  labels:
    service: write
- targets: [http://financial-read-api:5081/health/live]
  labels:
    service: read
```

No arquivo de readiness, use os mesmos hosts com `/health/ready`. Se usar endereços do host, `host.docker.internal` é mapeado para o endereço configurado em `MONITORING_HOST_ADDRESS`. Garanta que o listener e o firewall permitam acesso apenas da rede de monitoramento; não exponha uma API de desenvolvimento com a chave JWT previsível. O Prometheus atualiza os arquivos de descoberta a cada 30 segundos. A regra `FinancialApiProbesNotConfigured` avisa quando as sondagens das APIs ainda não foram cadastradas.

Abra `http://127.0.0.1:9090/alerts` e `http://127.0.0.1:9093` no próprio servidor. Os alertas cobrem indisponibilidade das APIs configuradas, falha TCP dos bancos, resposta de readiness degradada na leitura e latência acima da premissa de 500 ms. O Alertmanager não envia notificações externas por padrão; configure um receiver aprovado de e-mail, chat ou webhook antes de depender de aviso humano. Os limites são premissas operacionais do exercício, não SLOs aprovados. A configuração e a UI não fornecem alta disponibilidade para o próprio monitoramento.

Em 09/11/2026, o perfil foi iniciado no servidor `192.168.88.200` como projeto Compose isolado, sem recriar os bancos existentes. Prometheus, Blackbox Exporter e Alertmanager ficaram em execução. `promtool check config` confirmou o arquivo Prometheus e encontrou sete regras válidas; `amtool check-config` aceitou o arquivo do Alertmanager. A consulta Prometheus retornou `probe_success=1` para as portas 5432 e 5433, e os alvos apareceram como `up`. A primeira tentativa falhou porque os bancos publicavam portas somente no IP LAN e o gateway Docker não alcançava esse bind; depois de definir `MONITORING_HOST_ADDRESS=192.168.88.200`, as sondagens passaram. Como nenhuma API está implantada nesse host, `FinancialApiProbesNotConfigured` chegou ao Alertmanager conforme esperado. A integração ainda não foi testada com endpoints reais das APIs nem com notificações externas.

### Executar as APIs em containers de ensaio

O perfil `applications` constrói a API de escrita, a API de leitura e o projetor pelo `Dockerfile`. O perfil usa dois bancos novos (`financial_write_monitoring` e `financial_read_monitoring`) com papéis separados: `financial_write_app`/`financial_read_app` têm `SELECT`/`INSERT` e as atualizações necessárias, sem `DELETE` ou DDL; `financial_write_migrator`/`financial_read_migrator` são donos dos bancos e aplicam DDL. Não aponte esses serviços aos bancos funcionais nem ao ledger sintético existente. No servidor, copie o projeto e rode `scripts/Initialize-FinancialSandbox.sh` para gerar credenciais aleatórias e provisionar somente esses bancos dedicados. O script não redefine senhas nem altera bancos existentes; se encontrar provisionamento parcial, para e pede correção manual.

Aplique primeiro as migrations em um container descartável, usando os papéis de migração. Não use `up` sem `--no-deps`, pois os bancos já existentes pertencem à rede externa e não devem ser recriados por este Compose. Em seguida, inicie as APIs e o projetor; as APIs do perfil não aplicam migrations no startup:

```bash
docker compose --env-file /home/alexandre/financial-observability/.env --env-file .env -p financial-monitoring --profile migrations run --build --rm --no-deps financial-database-migrator
docker compose --env-file /home/alexandre/financial-observability/.env --env-file .env -p financial-monitoring --profile applications up -d financial-write-api financial-read-api
docker compose --env-file /home/alexandre/financial-observability/.env --env-file .env -p financial-monitoring --profile applications up -d financial-projector
docker compose --env-file /home/alexandre/financial-observability/.env --env-file .env -p financial-monitoring --profile applications --profile monitoring up -d financial-blackbox-exporter financial-alertmanager financial-prometheus
```

As APIs não publicam portas no host; suas portas 8080 ficam acessíveis apenas na rede interna do Compose para o monitoramento. O perfil usa `DOTNET_ENVIRONMENT=Development` para este ensaio isolado e JWT local aleatório; migrations são executadas separadamente. Ele não representa configuração de produção: antes de expor clientes, use provedor de identidade confiável, TLS validado para PostgreSQL, secret manager e uma implantação Production. Os endpoints de health são públicos dentro da rede e não devem ser publicados fora dela.

O primeiro deploy do sandbox, em 09/11/2026, ocorreu antes da separação dos papéis; naquela versão, os usuários `*_app` eram donos dos bancos. A migração desses containers existentes foi concluída em 09/11/2026 após backup e teste de restauração, com janela de manutenção autorizada. A transição preservou as senhas dos papéis de aplicação, criou credenciais novas para migration e transferiu propriedade e DDL para os papéis `*_migrator`. O roteiro executado foi atualizar as imagens com migrations no startup desativadas, parar APIs e projetor, executar `scripts/Harden-FinancialSandboxRoles.sh .env`, rodar o `financial-database-migrator` e reiniciar os serviços.

Em 09/11/2026, o script de hardening foi ensaiado em dois containers PostgreSQL 17 descartáveis, com bancos, papéis e linhas sintéticas. O teste reproduziu chaves de migration vazias em arquivo `.env` CRLF; a primeira execução revelou que o script acrescentava um valor sem substituir a linha vazia. O script foi corrigido para substituir entradas vazias, retirar `CR` ao ler valores e rejeitar chaves duplicadas. Na repetição, os dois bancos passaram a pertencer aos papéis migrator; os papéis runtime mantiveram `INSERT`/`UPDATE`, não puderam executar `DELETE` nem DDL, e os papéis migrator puderam criar objetos. Containers e arquivos descartáveis foram removidos. Essa simulação foi seguida pela migração do sandbox ativo descrita abaixo.

Em 09/11/2026, o `Financial.DatabaseMigrator` foi executado após o hardening nos bancos descartáveis: aplicou quatro migrations no banco de escrita e duas no de leitura. O dono dos bancos e das tabelas era o papel migrator; papéis runtime inseriram e atualizaram dados, sem poder apagar linhas ou criar objetos. Também foram validados privilégios padrão em tabelas criadas depois das migrations. O Npgsql 10 tenta GSSAPI por padrão ([documentação do Npgsql](https://www.npgsql.org/doc/security.html?tabs=tabid-2)); as conexões do sandbox definem `GSS Encryption Mode=Disable`, e a repetição terminou sem erros de negociação. A exigência de TLS `VerifyFull` para `Production` permanece.

Também em 09/11/2026, a migração foi aplicada ao sandbox ativo em `192.168.88.200`. Antes da parada, foi criado o backup `daily-20261109T142237Z` e validada a restauração de `financial_write_instrumented` em banco temporário, com checksum e contagens conciliados. Durante a janela, as duas APIs e o projetor foram parados; os dois bancos permaneceram disponíveis. O script de hardening concluiu e o migrador aplicou `001_InitialSchema`, `003_AuditAndDeadLetters`, `004_RemoveRedundantEntryIndexes` e `005_AccountLastOccurredAt` no banco de escrita, e `002_ReadProjection` e `004_RemoveRedundantEntryIndexes` no banco de leitura. Em seguida os três serviços foram reativados. Verificação posterior: os dois bancos `healthy`, seis probes Prometheus (`DB TCP`, readiness e liveness das APIs) com valor `1`, bases de monitoramento pertencentes aos papéis migrator e papéis `*_app` sem superuser, criação de banco ou criação de papel. A evidência prova a migração de papéis e recuperação operacional do sandbox; não prova HA entre hosts, failover automático nem SLA/RTO/RPO. O backup dessa execução era local e sem criptografia; armazenamento externo e proteção criptográfica ainda precisam ser configurados.

Como referência de operação e rollback, a sequência aplicada ao sandbox foi:

```bash
docker compose --env-file /home/alexandre/financial-observability/.env --env-file .env -p financial-monitoring --profile applications up -d --build financial-write-api financial-read-api financial-projector
docker compose --env-file /home/alexandre/financial-observability/.env --env-file .env -p financial-monitoring --profile applications stop financial-write-api financial-read-api financial-projector
bash scripts/Harden-FinancialSandboxRoles.sh .env
docker compose --env-file /home/alexandre/financial-observability/.env --env-file .env -p financial-monitoring --profile migrations run --build --rm --no-deps financial-database-migrator
docker compose --env-file /home/alexandre/financial-observability/.env --env-file .env -p financial-monitoring --profile applications up -d financial-write-api financial-read-api financial-projector
```

Em futuras migrações, confirme backup/restore e defina a janela de manutenção antes da primeira linha. Confirme readiness das APIs e heartbeat do projetor antes de encerrar a janela. Se algum passo falhar, mantenha os serviços parados e use o backup/runbook; não restaure propriedade ao papel de runtime sem investigar o erro.

No deploy original, com papéis ainda donos dos bancos, imagem .NET 10 e container não-root (`1654`), build e migrations concluíram; seis alvos de health retornaram `probe_success=1` e o heartbeat do projetor estava recente. A suíte integrada executada em container SDK 10.0 passou então com 28 testes e sete testes PostgreSQL sem skips. A separação de papéis foi posteriormente aplicada e verificada no host em 09/11/2026, conforme registrado acima.

Em 09/11/2026, o perfil foi iniciado no host Docker de ensaio, vinculado somente ao endereço privado desse host. A UI respondeu HTTP 200 e as portas OTLP gRPC/HTTP ficaram acessíveis. Com chaves diferentes para ingestão e consulta, uma API de escrita apontada para a base PostgreSQL sintética enviou traces, métricas e logs. A API de telemetria confirmou o recurso `Financial.write`, `hasTraces=true`, `hasMetrics=true`, `hasLogs=true`, 12 spans e um registro de log. Entre os spans recebidos estavam `financial.database.account_lock`, `financial.database.write_batch` e `financial.database.commit`. A requisição de smoke test criou apenas um crédito de um centavo na base dedicada; outra chamada intencional a uma conta inexistente gerou o log `404` sem registrar ID. O dashboard permanece sem volume de persistência, portanto seus dados somem quando o container é recriado; a configuração e as chaves ficam no diretório privado do host, fora do repositório.

O `docker-compose.yml` guarda dados em volumes locais sem replicação. Para reiniciar um ambiente de teste limpo, pare os containers e remova apenas os volumes deste compose depois de confirmar que não há dados necessários. Não use os comandos de limpeza local em ambiente compartilhado.

## Deploy e rollback

1. Verifique build, testes automatizados, migrations pendentes e compatibilidade da versão nova com o schema atual.
2. Faça backup/snapshot e confirme que o banco está saudável.
3. Aplique migrations como etapa controlada antes de ativar os novos processos. Configure `DOTNET_ENVIRONMENT=Production`, `ConnectionStrings__FinancialWriteMigrator` e `ConnectionStrings__FinancialReadMigrator` com as credenciais de migração em um gerenciador de segredos e execute `dotnet run --project src/Financial.DatabaseMigrator -- all`. As APIs e o projetor usam as credenciais distintas dos papéis `*_app`, sem permissão de DDL. O comando exige TLS com `SslMode=VerifyFull`; use `write` ou `read` para migrar apenas um banco.
4. Implante primeiro o projetor compatível com eventos atuais e próximos; depois leitura e escrita. Use endpoints de readiness antes de direcionar tráfego.
5. Acompanhe erros, latência, backlog e idade da projeção durante a liberação gradual.
6. Para rollback de aplicação, reimplante a versão anterior apenas se ela for compatível com as migrations aditivas já aplicadas. Não reverta schema automaticamente. Se for necessária restauração de banco, pare as gravações e siga o procedimento aprovado de recuperação, avaliando perda de dados desde o backup.

Não há pipeline de deploy nesta entrega; o ambiente e a estratégia de rollout de produção precisam ser definidos pela empresa.

## Backup e restore

Em produção, use backup gerenciado/PITR com criptografia, controle de acesso, retenção aprovada e cópia fora da zona de falha. Proteja separadamente os bancos de escrita e leitura. A projeção é reconstruível a partir da outbox retida; o ledger de escrita é a fonte autoritativa.

### Backups lógicos agendados do ambiente de ensaio

No servidor, `scripts/Backup-FinancialDatabases.sh` faz dumps custom-format de cada banco `financial*` nos containers PostgreSQL de escrita e leitura, exceto bases temporárias `financial_restore_validation_*`. Com age, `pg_dump` envia o fluxo diretamente para a cifra e nenhum dump em claro é criado como arquivo temporário; confirme a recuperação com o script de restore. Sem recipient, o script bloqueia a execução por padrão. O modo em claro só pode ser habilitado explicitamente por `FINANCIAL_BACKUP_ALLOW_PLAINTEXT=true`, exclusivamente para sandbox isolado com dados sintéticos; ele emite aviso e métricas registram `encryption_enabled=0`. O diretório `/home/alexandre/financial-backups` usa modo `700`, as pastas também `700`, e os artefatos/manifestos usam `600`. A retenção local é de 14 dias. Configure o job diário às 03:00 no fuso do servidor com:

```bash
bash scripts/Install-FinancialBackupCron.sh
```

Execute `bash scripts/Backup-FinancialDatabases.sh` uma vez para validar agora, sem aguardar a janela agendada. O perfil `monitoring` lê `monitoring/metrics/financial_backup.prom` pelo exporter de texto. As regras em `/alerts` sinalizam ausência de sucesso, falha da última execução ou atraso superior a 30 horas. O job mantém no Prometheus o último timestamp de sucesso mesmo se uma tentativa posterior falhar.

O script lê `FINANCIAL_BACKUP_AGE_RECIPIENT`, `FINANCIAL_BACKUP_ALLOW_PLAINTEXT`, `FINANCIAL_BACKUP_REMOTE_TARGET` e `RCLONE_CONFIG` do ambiente ou do `.env` do projeto. Quando há recipient age, cada dump é cifrado antes de ser publicado localmente. Quando também há destino remoto, o script exige recipient, envia apenas o pacote cifrado por `rclone copy` e confere os arquivos remotos com `rclone check --download --one-way`; se a cópia ou a conferência falhar, a execução é marcada como falha. O target deve ser um remote nomeado, por exemplo `arquivo:financeiro/backups`; configure as credenciais em arquivo separado apontado por `RCLONE_CONFIG`, com permissão `600`. Nunca coloque a identidade privada age no `.env` nem no host de backup.

Ao configurar o destino, gere o par de chaves com a ferramenta [age](https://github.com/FiloSottile/age), guarde a identidade privada no cofre/aparelho de recuperação e coloque somente o recipient público em `FINANCIAL_BACKUP_AGE_RECIPIENT`. Configure o remote e suas credenciais conforme a [documentação de configuração do rclone](https://rclone.org/docs/). `FINANCIAL_BACKUP_REMOTE_TARGET` aceita `remote:path`; `RCLONE_CONFIG` aponta ao arquivo de credenciais protegido. Não preencha esses valores com placeholders: destino, política de retenção e procedimento de recuperação precisam ser confirmados antes de ativar a cópia.

O `.env.example` deixa os parâmetros de recipient e destino vazios até que a empresa defina a chave e o armazenamento, e bloqueia dumps em claro por padrão. `age` e `rclone` só são necessários quando os respectivos parâmetros forem configurados. Ao ativar a cifra, os arquivos plaintext já existentes permanecem até sua expiração de retenção; revise-os e faça a limpeza controlada depois de confirmar que as cópias cifradas foram verificadas. A política de ciclo de vida/retention do destino remoto deve ser definida no próprio armazenamento. Enquanto não houver recipient e destino provisionados e ensaio de recuperação com a chave privada, a rotina continua sendo backup lógico local, não PITR; pode perder até um dia de alterações e depende do mesmo host. Portanto, ainda não atende RPO 0 nem recuperação após perda do servidor.

Em 09/11/2026, a primeira execução manual do job criou e validou nove dumps dos bancos `financial*`, totalizando aproximadamente 298 MB. Os arquivos e o manifesto ficaram com modo `600`, dentro de uma pasta `700`; cada dump passou pela leitura de catálogo do `pg_restore` e recebeu SHA-256. O cron foi instalado para 03:00 no fuso local do servidor. Após recriar o Prometheus para aplicar a nova configuração, o alvo `financial-backup` e as métricas de sucesso (`1`) e quantidade de bancos (`9`) foram coletados; as sondagens de APIs e bancos também ficaram `up`, sem alertas ativos. Esta verificação comprova execução e monitoramento da rotina, mas não comprova que a janela diária já ocorreu nem remove as limitações de cifragem e armazenamento no mesmo host.

Às 12:06Z de 09/11/2026, a versão com parâmetros opcionais gerou nove dumps locais e o verificador restaurou `financial_write_instrumented` com as contagens e somas esperadas; o SHA-256 foi `48d8dd1b3bde88e901efc27577494f7e4ed8636d20c947528569abe103360caf`. Com recipient/destino vazios, as métricas indicaram `encryption_enabled=0` e `remote_copy_enabled=0`. Um ensaio que informou só `FINANCIAL_BACKUP_REMOTE_TARGET` terminou com código `2`, sem enviar dados, pois não havia recipient age. Uma nova execução local válida restabeleceu `last_run_success=1`; Prometheus coletou ambas as métricas e não havia alertas ativos. Naquele momento, o caminho de age/rclone ainda não havia sido ensaiado; a validação posterior está descrita abaixo.

Às 12:47Z de 09/11/2026, `scripts/Test-FinancialEncryptedBackup.sh` cifrou nove backups em um diretório temporário, sem cópia remota, e confirmou `encryption=age` no manifesto e ausência de arquivos `.dump` em claro. O restore de `financial_write_instrumented` conferiu SHA-256 `1ec9d670ec85a2410ed367fc862d7875a3aa22469c8e64f50fbbc8ebe010ffc7`; origem e restauração coincidiram em 52 contas, 386.541 lançamentos, 386.541 centavos de saldo e de lançamentos, 386.593 eventos de outbox, 386.593 eventos de auditoria e quatro migrations. A identidade age e os backups temporários foram removidos; a base temporária de restore também foi removida. Esse ensaio valida cifragem e recuperação lógica local, mas não valida envio externo, gestão de chave duradoura, PITR ou recuperação após perda do host. A cópia externa ainda aguarda destino e recipient aprovados.

Para ensaiar a restauração de um arquivo diário, execute `bash scripts/Test-FinancialBackupArchive.sh` no host Docker. Por padrão, o script seleciona o dump de `financial_write_instrumented` da pasta diária mais recente. Ele confere o SHA-256 contra o manifesto, valida o catálogo, restaura em um banco temporário `financial_restore_validation_*`, compara contas, lançamentos, soma de saldos, soma dos lançamentos, outbox, auditoria e migrations com a origem e remove o banco temporário ao terminar. Arquivos age exigem `FINANCIAL_BACKUP_AGE_IDENTITY_FILE` apontando para a identidade privada, com permissão `600` ou `400`; mantenha essa chave no ambiente de recuperação aprovado e fora do host de backup. É possível informar uma pasta de backup como primeiro argumento ou definir `FINANCIAL_BACKUP_ROOT`, `FINANCIAL_WRITE_CONTAINER` e `FINANCIAL_RESTORE_SOURCE_DATABASE` para outro ambiente compatível.

Para validar a trilha cifrada, instale `age` no ambiente de ensaio e execute `bash scripts/Test-FinancialEncryptedBackup.sh`. O teste cria uma identidade age temporária, gera backups cifrados de todos os bancos `financial*` em diretório descartável, confirma que não há arquivos `.dump` em claro, valida checksum e restaura o banco indicado por `FINANCIAL_BACKUP_ENCRYPTION_TEST_DATABASE` (padrão `financial_write_instrumented`). Ele força destino remoto vazio, não lê nem altera as configurações remotas do `.env`, não toca nas métricas de produção e remove backups e identidade temporários ao final.

`Backup-FinancialDatabases.sh` aceita `FINANCIAL_BACKUP_ROOT` e `FINANCIAL_BACKUP_METRICS_FILE` como sobrescritas de ensaio; os padrões continuam sendo `/home/alexandre/financial-backups` e o arquivo de métricas do projeto. Assim o teste cifrado pode usar caminhos temporários sem alterar cópias retidas nem métricas monitoradas.

Em 09/11/2026, o verificador restaurou o dump `financial_write_instrumented` da pasta `daily-20261109T113430Z`. O SHA-256 conferido contra o manifesto foi `2449cbe69f8f26fb26543211043c82770352ae2d518108780fffd410a5759124`; os valores coincidiram: 52 contas, 386.541 lançamentos, 386.541 centavos no saldo agregado e nos lançamentos, 386.593 eventos de outbox, 386.593 eventos de auditoria e quatro migrations. O banco temporário foi removido pelo script. O ensaio continua limitado ao mesmo host e à mesma instância PostgreSQL.

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

Para repetir a validação no host Docker remoto, use `scripts/Test-FinancialBackupRestore.ps1`. O padrão aponta para `alexandre@192.168.88.200`, o container `financial-write-db` e o banco sintético dedicado `financial_write_instrumented`. O script cria um dump custom-format com permissão restrita, calcula SHA-256, valida a lista do arquivo, restaura em um banco novo com nome único e compara contagens e somas. Ele preserva o dump e o banco restaurado para inspeção; não remove nem substitui o banco de origem. Revise os parâmetros antes de apontar para outro ambiente.

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\scripts\Test-FinancialBackupRestore.ps1
```

Em 09/11/2026, a execução no servidor `192.168.88.200` criou um dump de 57.300.882 bytes e restaurou `financial_write_instrumented` em `financial_restore_validation_20261109_105419_f8175cdb4a`. O SHA-256 foi `a2e0bbe2c89b13e04900d762c36c6505f6f9598f55eedd29875185c5f8d429d5`. As contagens e somas coincidiram: 52 contas, 386.541 lançamentos, 386.593 eventos de outbox (2 pendentes) e 386.593 eventos de auditoria; referências órfãs: zero. O dump e a base restaurada foram mantidos no host para inspeção.

Este ensaio comprova backup lógico e restauração no mesmo host e instância PostgreSQL com dados sintéticos. Não comprova agendamento, criptografia do arquivo, cópia fora do host, PITR, failover ou recuperação após perda do servidor; portanto, não estabelece RPO/RTO de produção.

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

Em 09/11/2026, no deployment em containers do servidor `192.168.88.200`, a API de escrita foi parada por 20 segundos. Durante a parada, Prometheus registrou liveness e readiness da escrita como `0`; a API de leitura manteve ambas em `1`, e as sondas TCP dos dois bancos continuaram em `1`. A API de escrita foi reiniciada e, após o próximo ciclo de sondagem, todas as seis sondas retornaram `1`; nenhum alerta permaneceu ativo. O exercício não parou o banco nem executou consulta autenticada de saldo, pois as bases deste deployment estão vazias. A continuidade da rota de negócio com saldo projetado é coberta pelos testes de integração PostgreSQL descritos acima.

Em 08/11/2026, a suíte `PostgresFinancialRepositoryTests` foi executada usando bancos dedicados e separados para escrita (`financial_write_loadtest`) e leitura (`financial_read_pipeline_test`); schemas temporários foram removidos ao final. Os sete testes passaram, cobrindo leitura consolidada após o encerramento da API de escrita, leitura sem conexão de escrita, replay idempotente da projeção, concorrência e retries idempotentes. O teste com instâncias independentes de `WebApplicationFactory` e o teste com dois processos reais da API compartilharam o mesmo banco de escrita; cada cenário enviou 100 débitos simultâneos contra saldo inicial de 10.000 centavos e confirmou 50 operações aceitas, 50 rejeitadas por saldo insuficiente, saldo final zero e 51 lançamentos. O workflow de CI agora provisiona dois serviços PostgreSQL independentes e entrega suas conexões separadas à suíte. Isso valida o caminho de integração entre instâncias e bancos distintos, mas não simula queda do servidor PostgreSQL ou failover de infraestrutura.

Em 09/11/2026, a suíte foi ampliada para encerrar um processo real da API de escrita enquanto a API de leitura e os dois bancos independentes permaneciam ativos. A consulta de saldo continuou respondendo `200` com 1.250 centavos, embora a API de leitura recebesse uma conexão de escrita inválida. Após iniciar um novo processo de escrita e registrar mais 500 centavos, a leitura continuou disponível com a projeção anterior até o evento pendente ser processado; em seguida, mostrou 1.750 centavos. O build Release com `-warnaserror` terminou com zero avisos e erros; os 30 testes passaram. O ensaio criou bancos e credencial temporários, distintos dos bancos da aplicação, e os removeu ao terminar. A projeção foi processada diretamente pelo componente `PostgresOutboxProjector`; este teste não derruba o PostgreSQL, o host nem exercita failover físico ou RTO/RPO.

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

Em 08/11/2026, o projetor foi alterado para processar cada lote de até 500 eventos com operações SQL set-based. Em novo ensaio na mesma infraestrutura, a API registrou 30.000 débitos em 60,03 s (499,7/s), todos `201`; p50 26,4 ms, p95 177,8 ms, p99 605,8 ms e máximo 897,9 ms. Havia 9 eventos pendentes ao fim da carga, e a fila chegou a zero na primeira verificação cinco segundos depois. Para o titular sintético do ensaio, escrita e leitura conferiram 50 contas, 30.050 lançamentos e saldo de 20.000 centavos; a consulta HTTP consolidada retornou o mesmo saldo. Esta execução de um minuto mostra o pipeline acompanhando esta carga no host, sem validar o ensaio de cinco minutos, metas ainda não aprovadas, failover ou outra infraestrutura.

Na repetição de cinco minutos após a otimização, foram concluídas 150.000 escritas em 301,67 s (497,23/s), todas `201`. Latências: p50 31,68 ms, p95 484,52 ms, p99 1.403,28 ms e máximo 3.859,14 ms. Ao fim, havia 487 eventos pendentes; a fila chegou a zero na verificação de cinco segundos seguinte. A reconciliação encontrou 50 contas, 150.050 lançamentos e saldo de 350.000 centavos nas bases de escrita e leitura; a consulta HTTP retornou os mesmos 350.000 centavos. O pipeline acompanhou o volume total, mas a latência subiu em relação ao ensaio de um minuto. P95 de 300 ms e p99 de 1 s continuam referências provisórias e não foram atingidos nesta execução sustentada; investigar saturação e repetir com tracing antes de concluir o dimensionamento.

A API também publica spans `financial.database.account_lock`, `financial.database.write_batch` e `financial.database.commit`, com histogramas `financial.database.account_lock.duration`, `financial.database.write_batch.duration`, `financial.database.commit.duration` e `financial.database.write_batch.commands`. Configure `OTEL_EXPORTER_OTLP_ENDPOINT` para enviar traces e métricas a um backend e comparar a duração e os erros dessas etapas com a latência HTTP. Os atributos não incluem dados ou identificadores financeiros.

Em 09/11/2026, uma nova execução de cinco minutos foi feita com o gerador `tools/Financial.LoadTest`, 50 contas sintéticas, concorrência 150 e taxa programada de 500 solicitações/s. A API e o projetor rodaram no cliente de ensaio; os PostgreSQL de escrita e leitura eram containers separados em outro host da rede local. Foram aceitos 149.999 créditos de um centavo em 300,29 s (499,52/s), todos `201`, sem falhas de transporte. Latências da API: p50 12,99 ms, p95 155,25 ms, p99 600,02 ms e máximo 1.524,45 ms. Assim, esta execução atingiu as metas provisórias de p95 300 ms e p99 1 s; o máximo evidencia picos e uma única execução não valida um SLO de produção.

Como diagnóstico distinto, uma execução preliminar de 60 s enviou 7.941 operações para uma única conta: 129,91/s, p50 918,99 ms, p95 2.371,73 ms e p99 3.405,15 ms, todas `201`. Esse cenário concentra todas as transações no mesmo lock por conta e mostra contenção; não é comparável ao perfil de 50 contas nem representa a mesma taxa-alvo.

O projetor manteve o backlog próximo de zero e terminou com zero eventos pendentes. A reconciliação dos dados sintéticos encontrou 51 contas, 386.540 lançamentos e saldo agregado de 386.540 centavos em cada banco; a conta adicional e parte dos lançamentos vieram do ensaio preliminar de contenção em uma única conta. A carga de cinco minutos foi distribuída apenas pelas outras 50 contas. Amostras de `docker stats` durante o ensaio mostraram cerca de 328–356 MiB no container de escrita e 175–179 MiB no de leitura. `dotnet-counters` coletou 301 janelas de um segundo; a mediana do p95 por janela foi 7,29 ms no lock da conta, 7,77 ms no lote SQL e 9,89 ms no commit. Os maiores p95 de uma janela isolada foram 948 ms, 64,12 ms e 1.032 ms, respectivamente, então os sinais apontam para picos esporádicos de lock/commit que merecem correlação em tracing OTLP. Nesta execução, as métricas foram coletadas por `dotnet-counters`; os spans ainda não foram enviados a um backend OTLP.

O gerador distribui as escritas em round-robin por IDs explícitos, aceita taxa-alvo e duração, e exige `--allow-write-load`. Cada solicitação cria um crédito sintético de um centavo que permanece no banco; execute apenas contra API e contas de teste. Exemplo para uma base isolada já provisionada:

```powershell
$env:FINANCIAL_LOADTEST_TOKEN = .\scripts\New-LocalJwt.ps1 -Subject "loadtest" -LifetimeMinutes 10
dotnet run --project tools/Financial.LoadTest/Financial.LoadTest.csproj -- --allow-write-load --base-url http://localhost:5082 --account-ids "<guid-1>,<guid-2>" --duration 00:05:00 --concurrency 150 --requests-per-second 500
```

O ensaio não incluiu consultas HTTP de leitura durante a carga nem falha deliberada de PostgreSQL, failover, backup/restore ou perda do host. Ele valida latência de escrita e drenagem da projeção neste ambiente específico; não comprova RTO/RPO nem disponibilidade de infraestrutura.

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

## Reconciliação periódica do ledger e da projeção

Execute `bash scripts/Reconcile-FinancialProjection.sh` no host Docker. Por padrão, o script usa `financial` e `financial_read`; defina `FINANCIAL_WRITE_DATABASE` e `FINANCIAL_READ_DATABASE` para apontar a outro par. Ele confere se o saldo de cada conta é igual à soma dos lançamentos em cada banco e compara os IDs dos eventos já processados/projetados há pelo menos cinco minutos. Os conjuntos são capturados duas vezes; se mudarem durante a verificação, o resultado é inconclusivo e o script pede nova tentativa. Eventos mais novos que a janela de estabilização ficam fora da comparação para respeitar a consistência eventual.

Para agendar a verificação a cada cinco minutos, use os nomes dos bancos da aplicação ativa. No host de ensaio atual:

```bash
FINANCIAL_WRITE_DATABASE=financial_write_monitoring \
FINANCIAL_READ_DATABASE=financial_read_monitoring \
bash scripts/Install-FinancialReconciliationCron.sh
```

Cada execução atualiza atomicamente `monitoring/metrics/financial_reconciliation.prom`, consumido pelo exporter de texto já usado para backup. As métricas registram sucesso, hora, divergências de saldo, diferença de IDs, backlog e dead letters; não contêm IDs de clientes ou contas. Prometheus alerta se nunca houve sucesso, se a última execução falhou ou se nenhuma execução ocorreu em dez minutos. O cron grava saída em `/home/alexandre/financial-backups/financial-reconciliation-cron.log`; confirme a linha instalada com `crontab -l | grep Reconcile-FinancialProjection.sh`.

Em 09/11/2026, o job foi instalado no servidor para os bancos isolados `financial_write_monitoring`/`financial_read_monitoring`. Uma execução manual e uma execução posterior do cron escreveram métricas; Prometheus coletou `financial_reconciliation_last_run_success=1`, backlog zero e manteve o alvo exporter `up`. `promtool` validou a configuração e as 14 regras carregadas, incluindo os três novos alertas. O ensaio confirma o agendamento e o caminho de métricas; as bases ainda estavam vazias e não comprovam reconciliação de movimentações de produção.

O script também informa backlog e dead letters. Backlog recente é reportado como atraso esperado da projeção; backlog mais velho que cinco minutos, qualquer dead letter, divergência de saldo ou diferença entre conjuntos maduros retorna erro. A janela e os limites podem ser configurados por `FINANCIAL_RECONCILIATION_SETTLE_SECONDS`, `FINANCIAL_RECONCILIATION_MAX_PENDING_AGE_SECONDS`, `FINANCIAL_RECONCILIATION_RETRIES` e `FINANCIAL_RECONCILIATION_RETRY_DELAY_SECONDS`. Execute com acesso somente leitura aos dados de negócio sempre que possível; o script não altera o ledger ou a projeção.

Em 09/11/2026, o ensaio no par ativo isolado `financial_write_monitoring`/`financial_read_monitoring` encontrou zero divergências e zero backlog, mas essas bases estavam vazias, portanto o resultado é apenas uma verificação operacional básica. No par sintético antigo `financial_write_instrumented`/`financial_read_instrumented`, o script conferiu 386.591 IDs maduros e não encontrou divergência de saldo, mas detectou dois eventos pendentes há 3.444 segundos e terminou com código `1` por exceder o limite de cinco minutos. Esse par não é usado pelo serviço ativo; o achado mostra que o verificador sinaliza backlog persistente sem classificar a projeção antiga como totalmente consistente.

## Failover do PostgreSQL

O servidor de ensaio não possui standby nem mecanismo de promoção. Se o banco de escrita ficar indisponível, não confirme movimentações; a API de leitura pode continuar exibindo a projeção com seu `dataAsOf`. Não promova manualmente outro banco lógico nem direcione a API para a projeção de leitura como se fosse o ledger autoritativo. Após recuperar/restaurar o primário, verifique migrations, reconciliação do ledger e backlog da outbox antes de liberar gravações.

A topologia proposta para produção e os exercícios necessários estão em [`ADR-0001`](decisions/adr-0001-postgresql-high-availability.md). Só execute promoção quando existir uma standby configurada e o procedimento conseguir cercar o primário antigo; o PostgreSQL fornece replicação e promoção, mas o detector/fencing precisam vir do serviço gerenciado ou do coordenador HA aprovado. Registre RPO observado, tempo de detecção/promoção/reconexão, erros e resultado da reconciliação no ensaio.

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

O ambiente de ensaio tem backup lógico local e reconciliação agendados, alertas de execução e verificadores manuais de restore. Os parâmetros para backup cifrado externo estão disponíveis, porém vazios; os dumps atuais não são cifrados nem copiados para fora do host. Não há PITR, replicação ou failover automatizado do PostgreSQL. A proposta de HA ainda depende de plataforma/provedor e de drill aprovado conforme [`ADR-0001`](decisions/adr-0001-postgresql-high-availability.md). As regras Prometheus estão provisionadas, mas não existe receiver externo de alertas. Também faltam deploy gradual e evidência multi-host. A reconciliação verifica saldos e cobertura de eventos da projeção; uma reconciliação financeira de negócio com extratos externos e tratamento de diferenças ainda requer regra e processo aprovados.
