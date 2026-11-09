# Requisitos e decisões da revisão

Este documento transforma o enunciado e a devolutiva da avaliação em requisitos verificáveis para a próxima versão. Ele distingue requisitos expressos pela empresa de premissas adotadas para orientar o exercício. As metas numéricas abaixo são propostas para validação; não foram informadas pela empresa e não devem ser apresentadas como compromisso operacional aprovado.

## Escopo funcional

| ID | Requisito | Decisão para esta revisão |
| --- | --- | --- |
| RF-01 | Registrar créditos e débitos em contas | Todo lançamento aceito é imutável e identificado por uma chave de idempotência dentro da conta. |
| RF-02 | Consultar saldo em um instante | A posição histórica considera os lançamentos efetivados até o instante consultado. |
| RF-03 | Consolidar saldo do titular | Soma as contas do titular somente quando todas compartilham a moeda assumida (BRL). |
| RF-04 | Evitar inconsistência financeira | Lançamento, atualização do saldo contábil e evento de publicação são confirmados atomicamente no serviço de escrita. |
| RF-05 | Restringir acesso | O titular autenticado só consulta e movimenta contas associadas à sua identidade opaca. |

## Premissas de domínio

- O escopo inicial cobre contas em BRL, representadas em centavos inteiros. Não há conversão cambial nem soma entre moedas.
- O saldo retornado é **saldo contábil**. Reservas, saldo disponível, limites de crédito e liquidação externa ficam fora do escopo.
- Um lançamento confirmado não é editado nem removido. Correções são lançamentos compensatórios vinculados ao lançamento original.
- A API atribui o instante de registro. Lançamentos retroativos e data-valor não são aceitos nesta revisão.
- A outbox retém eventos processados para permitir replay da projeção. Esta versão não implementa arquivamento/expurgo; o crescimento e a retenção precisam de política aprovada antes de produção.
- Débitos não podem deixar o saldo contábil negativo. Essa regra é premissa do exercício e deverá ser revista se o produto admitir limite ou saldo disponível separado.
- Não há transferência entre contas, iniciação de pagamentos ou integração externa neste escopo.

## Consistência e indisponibilidade

1. A confirmação de uma movimentação significa que lançamento, saldo contábil e evento na outbox foram gravados na mesma transação do banco de escrita.
2. Repetir uma solicitação com a mesma chave e os mesmos dados retorna o resultado já confirmado. Reutilizar a chave com conteúdo diferente é conflito. A unicidade é garantida pelo banco, inclusive entre processos.
3. A consulta consolidada usa uma projeção de leitura separada e é **eventualmente consistente**. Ela informa o instante/posição aplicada para que o cliente conheça a atualidade do dado.
4. Com os componentes saudáveis, a meta provisória de atualização da projeção é até 30 segundos, conforme o desafio revisado. Acima de 5 minutos, a API sinaliza estado degradado e a operação gera alerta. Esses limites precisam ser validados pelo negócio.
5. Se a API de escrita ou o banco de escrita estiver indisponível, novas movimentações falham sem confirmação. A consulta continua atendendo a última projeção disponível e identifica que ela pode estar atrasada.
6. Se a projeção de leitura estiver indisponível, a API de escrita continua aceitando movimentações confirmadas; elas permanecem na outbox até a projeção recuperar. A leitura não promete atualização enquanto estiver indisponível.
7. Eventos são entregues pelo menos uma vez. O consumidor precisa ser idempotente; ordem e versão por conta evitam aplicar atualizações antigas sobre mais recentes.

## Objetivos operacionais provisórios

Os valores são um cenário de dimensionamento e teste para a revisão técnica. Precisam de validação do responsável pelo produto/negócio antes de serem tratados como SLO de produção.

| Indicador | Alvo proposto | Observação |
| --- | --- | --- |
| Disponibilidade mensal da API de escrita | 99,9% | Exclui manutenção aprovada; definição final depende do negócio. |
| Disponibilidade mensal da consulta | 99,9% | O serviço de leitura deve ter implantação e armazenamento independentes da API de escrita. |
| Latência de escrita | p95 ≤ 300 ms; p99 ≤ 1 s | Medida na API, sem contar latência do cliente. |
| Latência de consulta | p95 ≤ 500 ms; p99 ≤ 500 ms | Consulta por conta e consolidada sob a carga de referência; p95 alinhado ao desafio revisado. |
| Carga de referência | 100 operações/s sustentadas; pico de 500 operações/s por 5 min | Premissa de teste, não volume fornecido pela empresa. |
| Retenção de lançamentos | Permanente durante a vida da conta | Alteração requer política legal e de negócio; não apagar o ledger por política genérica de retenção. |
| RPO de lançamentos confirmados | 0 | Uma resposta de sucesso só pode ocorrer após commit durável segundo a configuração de alta disponibilidade do banco. |
| RTO da escrita | até 30 min | Meta proposta para falha do serviço ou perda de instância; requer failover/restore testado. |
| RTO da consulta | até 15 min | A projeção pode ser reconstruída; leitura degradada deve ser sinalizada. |
| Recuperação da projeção | até 15 min para 1 h de backlog | Premissa inicial a medir com volume de referência. |

## Decisões arquiteturais desta revisão

- **Persistência de escrita:** PostgreSQL relacional, com transação ACID, migrations, chave única `(AccountId, IdempotencyKey)` e atualização concorrente protegida por lock/versionamento no registro da conta.
- **Separação de leitura:** API de consulta e banco de projeção próprios. Um projetor independente lê a outbox persistida com a movimentação e atualiza a projeção idempotentemente.
- **Publicação confiável:** transactional outbox elimina o intervalo entre commit financeiro e publicação do evento. O projetor usa checkpoint/versão e pode repetir eventos com segurança.
- **Disponibilidade:** implantação independente reduz falhas de processo e deploy compartilhados. Em produção, a meta proposta é banco de escrita com standby síncrono e failover/fencing gerenciados em domínios distintos; o banco de leitura precisa de HA própria e é reconstruível pela outbox. O desenho e os critérios de aceite estão em [`ADR-0001`](decisions/adr-0001-postgresql-high-availability.md). Dois bancos lógicos ou containers no mesmo servidor não são independência de infraestrutura; a ADR não está provisionada nem validada.
- **JSON:** deixa de ser a persistência operacional da solução revisada. Pode continuar como formato de exportação/fixture de teste, mas não como ledger concorrente.
- **Sem garantia inventada:** valores de SLA/SLO, volume, latência, RTO e RPO são hipóteses de trabalho até validação da empresa.

### Justificativa e trade-offs

- **PostgreSQL em vez de arquivo JSON:** transações, constraints de unicidade e locks por conta protegem o ledger entre processos e instâncias. O arquivo exigia carregar e regravar todo o estado e não coordenava múltiplas instâncias. O custo é operar e recuperar o banco; backup e restore precisam ser ensaiados.
- **Projeção de leitura separada:** a consulta não depende do processo nem da conexão do serviço de escrita e pode continuar servindo a última posição. O preço é consistência eventual, armazenamento adicional e necessidade de monitorar e reconstruir a projeção.
- **Outbox transacional em vez de publicar diretamente ou introduzir um broker:** o evento é confirmado no mesmo commit do lançamento, evitando a janela em que o ledger grava e a publicação falha. Um worker independente lê essa outbox e aplica eventos idempotentemente. Para o escopo do desafio, isso evita operar um broker sem remover a necessidade de tratamento de retry e backlog; vários consumidores independentes ou requisitos maiores de vazão podem justificar uma plataforma de eventos depois.
- **Mesma API com papéis e processos separados:** os modos `write` e `read` reutilizam contratos e autenticação, enquanto processos, conexões e bancos separados permitem falhas de processo isoladas. Isso não protege contra a perda do host nem substitui deploy multi-zona.
- **Metas propostas, não declaradas como SLA empresarial:** sem perfil de carga, orçamento e domínio de falha fornecidos, os números servem como alvos de ensaio. Só devem virar compromisso após validação do negócio e medição na infraestrutura escolhida.

## Rastreabilidade da devolutiva

| Ponto levantado | Decisão e evidência no projeto | Situação e limite explícito |
| --- | --- | --- |
| Requisitos e documentação | Requisitos funcionais, premissas, consistência e alvos numéricos estão neste documento; desenho em [`architecture.md`](architecture.md), operação em [`runbook.md`](runbook.md) e HA em [`ADR-0001`](decisions/adr-0001-postgresql-high-availability.md). | Documentado para avaliação. Valores de SLA/SLO, RTO/RPO e carga continuam premissas sem aprovação da empresa. |
| Disponibilidade da consolidação | API e banco de leitura independentes; o teste `Read_api_stays_available_during_write_process_outage_and_catches_up_after_recovery` encerra um processo real de escrita, lê 1.250 centavos durante a parada e observa 1.750 após processar o evento pendente. Evidência de execução no [`runbook.md`](runbook.md). | A falha de processo está comprovada. Queda do PostgreSQL, host, zona ou failover multi-host não estão comprovados; os dois bancos do sandbox compartilham o mesmo servidor. |
| Cobertura de testes | Testes de regras, autorização, idempotência, concorrência entre instâncias/processos, replay e projeção; ensaios de carga e backup/restore registrados no [`runbook.md`](runbook.md). O CI executa restore, build e testes. | A suíte completa executada com PostgreSQL passou com 30 testes no ensaio registrado. Falhas físicas de banco, failover, split brain e carga durante failover continuam fora da evidência atual. |
| Observabilidade e operação | Health/readiness, atualidade da projeção, métricas de transação e outbox, logs correlacionáveis, OpenTelemetry, alertas e runbooks. Consulte [`architecture.md`](architecture.md) e [`runbook.md`](runbook.md). | Instrumentação e alertas locais estão configurados; receiver externo, retenção de telemetria e gestão operacional de produção dependem da plataforma. |
| Segurança | Escopos JWT, isolamento por titular, rate limits, limite de payload e auditoria; o hardening foi aplicado aos bancos de monitoramento do sandbox em 09/11/2026. Os papéis runtime não são donos dos bancos e não têm superuser, criação de banco ou de papéis; o migrator separado é dono e aplica DDL. | A separação de papéis foi comprovada nesse host. Secret manager, rotação real, TLS e criptografia em repouso dependem da plataforma; o backup usado na migração foi local e sem criptografia. |
| Decisões de domínio e consistência | Moeda BRL, saldo contábil, compensação por novo lançamento, sem retroatividade e leitura eventualmente consistente estão declarados aqui; arquitetura de outbox e HA está justificada. | São premissas do exercício, não regras de produto confirmadas. A empresa deve validar moeda, estornos, reservas, atraso tolerado e comportamento de indisponibilidade. |
| Persistência e recuperação | PostgreSQL protege gravações com transação, constraints e lock; o ledger é fonte autoritativa, e a projeção pode ser refeita da outbox. O restore local foi ensaiado com backup age cifrado e backup explícito de sandbox em claro; sem recipient, o script agora bloqueia plaintext por padrão. | O sandbox configurou explicitamente a exceção plaintext até provisionar age. Backup externo/PITR, política de retenção aprovada e failover não estão implementados no host de ensaio. Consulte os limites do [`runbook.md`](runbook.md). |

## Segurança e operação assumidas

- JWT será validado por emissor confiável; autorização seguirá o titular da conta e políticas/perfis necessários. Para desenvolvimento local, scripts geram uma chave aleatória de 256 bits guardada fora do repositório com permissões restritas do usuário; a API recusa a chave simétrica local fora de `Development`.
- Segredos de ambiente real vêm de um secret manager, com rotação; dados financeiros e identificadores pessoais não aparecem em logs.
- TLS protege tráfego. Criptografia em repouso é responsabilidade do banco/armazenamento gerenciado e das chaves administradas.
- Conexões do sandbox usam senha e definem `GSS Encryption Mode=Disable` para não iniciar uma negociação Kerberos que o ambiente não provisiona. Isso não substitui TLS: conexões fora de `Development` continuam exigindo `SslMode=VerifyFull`. Se a plataforma aprovada fornecer GSS/Kerberos, a opção poderá ser configurada explicitamente.
- Auditoria registra quem executou ou consultou uma operação, quando e o resultado, com acesso e retenção controlados.
- Health checks distinguem liveness de readiness; métricas, logs correlacionáveis e tracing expõem latência, erro, backlog e idade da projeção.
- Backup só é considerado válido após restauração ensaiada. Runbooks cobrem deploy, rollback, falha de banco, atraso de projeção, fila/outbox, reconciliação e recuperação.

## Critérios de aceite da revisão

- Uma movimentação nunca deixa lançamento, saldo e outbox parcialmente persistidos.
- Duas instâncias concorrentes não conseguem aprovar débitos além do saldo nem duplicar a mesma chave idempotente.
- A API de consulta continua atendendo durante a parada da API de escrita, retornando a última projeção e sua atualidade.
- Reprocessar eventos não duplica saldo; evento fora de ordem não regride a projeção.
- Métricas e health checks mostram indisponibilidade/degradação e atraso do projetor sem expor dados sensíveis.
- Testes e instruções de operação demonstram persistência, concorrência, falha parcial e restauração. As metas numéricas são medidas em ambiente controlado e identificadas como alvos do exercício.
- RPO/RTO de produção só podem ser declarados após failover/restore ensaiados em domínios de falha independentes; a configuração isolada não constitui evidência de HA.
