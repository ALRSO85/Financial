# ADR-0001: Alta disponibilidade do PostgreSQL

| Campo | Valor |
| --- | --- |
| Estado | Proposta para validação; ainda não implementada no host de ensaio. |
| Data | 09/11/2026 |
| Decisão pendente de | Responsáveis de produto, arquitetura e plataforma |

## Contexto

A aplicação separa API de escrita, API de leitura, projetor e bancos PostgreSQL. O serviço de leitura pode continuar respondendo com a última projeção quando a API ou o banco de escrita para. Porém, os dois containers PostgreSQL disponíveis estão no mesmo host Docker, sem réplica, failover ou domínio de falha independente. A separação atual protege contra algumas falhas de processo; não protege contra perda do host nem satisfaz, sozinha, o objetivo provisório de disponibilidade de 99,9%.

As metas de avaliação registradas em [`requirements-and-decisions.md`](../requirements-and-decisions.md) são premissas: RPO 0 para movimentações confirmadas, RTO de até 30 minutos para escrita e 15 minutos para leitura. A empresa ainda não escolheu provedor, região, zonas, custo, janelas de manutenção ou política de degradação.

## Decisão proposta

Para escrita, usar PostgreSQL gerenciado com primário e standby síncrono em domínios de falha distintos. A aplicação só deve responder sucesso depois do commit durável e da confirmação da standby (`synchronous_standby_names` e `synchronous_commit=on`, ou controles equivalentes documentados pelo provedor). Se não houver standby elegível, a operação falha sem confirmar o lançamento; a durabilidade não deve ser reduzida silenciosamente para preservar disponibilidade.

A promoção deve ser gerenciada pelo provedor ou por um coordenador HA aprovado. O serviço precisa manter um endpoint estável e cercar o primário antigo antes de aceitar escritas no promovido. A replicação, por si só, não detecta uma falha nem escolhe o primário. Antes de reintegrar o nó antigo, é necessário ressincronizá-lo.

O banco de leitura deve ter implantação e HA próprias, adequadas ao SLO aprovado. A projeção pode ser refeita a partir da outbox retida; durante a recuperação, a API informa `dataAsOf` e o estado de prontidão. As APIs devem rodar em várias instâncias stateless atrás de um balanceador com readiness probes. O worker pode ter mais de uma instância se a exclusão e a idempotência continuarem garantidas pelo PostgreSQL. Clientes devem repetir escritas com a mesma chave idempotente após timeout ou reconexão.

Os backups e o PITR, quando disponíveis, devem ficar cifrados fora das zonas do primário e da standby. A réplica reduz o tempo de promoção, mas não protege contra corrupção lógica, exclusão ou comprometimento de credenciais.

PostgreSQL oferece replicação síncrona como mecanismo para esperar a confirmação da standby, e a configuração de failover precisa de software/procedimento externo que detecte a queda, promova a standby e impeça split brain. Consulte a [documentação de replicação síncrona](https://www.postgresql.org/docs/17/warm-standby.html#SYNCHRONOUS-REPLICATION) e a [documentação de failover](https://www.postgresql.org/docs/17/warm-standby-failover.html).

## Consequências e trade-offs

A confirmação síncrona acrescenta latência de rede e pode reduzir a disponibilidade de escrita quando a standby ou o link interzonal falha. Nesse caso, a aplicação deve deixar de confirmar operações cuja durabilidade não foi atendida. RPO 0 só vale para commits confirmados pela réplica promovida; esta configuração local não oferece essa garantia.

O RTO depende do tempo de detecção, fencing, promoção, atualização do endpoint, reconexão dos pools e validação. Deve ser medido em um exercício, não deduzido da configuração. A leitura pode ficar atrasada; sem uma decisão explícita de consistência, a API não deve consultar o banco de escrita como fallback. Uma réplica no mesmo host também não protege contra a perda desse host ou da zona.

## Critérios de aceite antes de afirmar HA

| Ensaio | Evidência exigida |
| --- | --- |
| Parada abrupta do primário | Novas gravações não recebem sucesso durante a eleição; a API de leitura continua respondendo com a última projeção. |
| Promoção da standby | Endpoint estável aponta para o novo primário; aplicações reconectam dentro do RTO definido. |
| Durabilidade | Todas as movimentações que receberam sucesso antes da falha existem no primário promovido; reconciliação de ledger e outbox sem divergência. |
| Split brain | Primário antigo fica cercado/fenced e não aceita nova escrita após promoção. |
| Retorno do antigo primário | Ele é reintroduzido como standby após ressincronização; não volta a aceitar escrita por conta própria. |
| Falha da projeção | Escrita confirmada continua; outbox acumula e drena sem duplicar saldo depois da recuperação. |
| Restore/PITR | Restauração em ambiente isolado, com tempos medidos, contagens e saldos reconciliados. |
| Carga durante failover | p95/p99, erros e backlog medidos contra SLO aprovado, sem atribuir garantias com premissas ainda não aprovadas. |

## Escopo ainda necessário

O host atual não fornece uma segunda zona nem um serviço PostgreSQL gerenciado. Portanto, esta ADR orienta a implantação e a avaliação da arquitetura; não demonstra replicação, promoção automática, fencing, RPO 0, RTO ou disponibilidade mensal. Esses critérios só podem ser fechados depois que a empresa escolher/provisionar a plataforma e autorizar um exercício de failover.
