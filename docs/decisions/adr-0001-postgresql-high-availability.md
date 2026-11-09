# ADR-0001: Alta disponibilidade do PostgreSQL

- **Status:** proposta para validação do negócio e da plataforma; não implementada no host de ensaio.
- **Data:** 09/11/2026.
- **Decisores pendentes:** responsáveis técnicos e de produto da empresa.

## Contexto

A aplicação separa API de escrita, API de leitura, projetor e bancos PostgreSQL. O serviço de leitura pode continuar respondendo com a última projeção quando a API ou o banco de escrita para. Porém, os dois containers PostgreSQL disponíveis estão no mesmo host Docker, sem réplica, failover ou domínio de falha independente. A separação atual protege contra algumas falhas de processo; não protege contra perda do host nem satisfaz, sozinha, o objetivo provisório de disponibilidade de 99,9%.

As metas de avaliação registradas em [`requirements-and-decisions.md`](../requirements-and-decisions.md) são premissas: RPO 0 para movimentações confirmadas, RTO de até 30 minutos para escrita e 15 minutos para leitura. A empresa ainda não escolheu provedor, região, zonas, custo, janelas de manutenção ou política de degradação.

## Decisão proposta

1. **Banco de escrita:** usar PostgreSQL gerenciado com primário e standby síncrono em domínios de falha distintos. Só responder sucesso após commit durável no primário e confirmação síncrona do standby (`synchronous_standby_names` e `synchronous_commit=on`, ou controles equivalentes documentados pelo serviço gerenciado). Se não houver standby elegível, a política financeira é falhar/aguardar sem confirmar novos lançamentos; não reduzir silenciosamente a durabilidade para preservar disponibilidade.
2. **Promoção e fencing:** usar o failover gerenciado pelo provedor ou um coordenador HA aprovado, com endpoint estável e fencing do primário antigo antes de aceitar escrita no promovido. A replicação do PostgreSQL, isoladamente, não detecta falha nem escolhe com segurança quem deve ser primário. A recuperação do antigo primário deve incluir ressincronização antes de reintegrá-lo.
3. **Banco de leitura:** manter datastore e deployment independentes da escrita, com HA própria apropriada ao SLO. A projeção é reconstruível a partir da outbox retida; durante a recuperação, a API expõe `dataAsOf` e o estado de prontidão/degradação.
4. **Aplicações:** executar múltiplas instâncias stateless de cada API atrás de load balancer com probes de readiness. O worker pode ter mais de uma instância, mantendo exclusão/idempotência no PostgreSQL. Requisições de escrita usam idempotency key também após timeout e reconexão.
5. **Recuperação:** manter backups cifrados/PITR em armazenamento fora das zonas primária e standby. A réplica reduz o tempo de promoção, mas não substitui backup contra corrupção lógica, exclusão ou comprometimento de credenciais.

PostgreSQL oferece replicação síncrona como mecanismo para esperar a confirmação da standby, e a configuração de failover precisa de software/procedimento externo que detecte a queda, promova a standby e impeça split brain. Consulte a [documentação de replicação síncrona](https://www.postgresql.org/docs/17/warm-standby.html#SYNCHRONOUS-REPLICATION) e a [documentação de failover](https://www.postgresql.org/docs/17/warm-standby-failover.html).

## Consequências e trade-offs

- A confirmação síncrona adiciona latência de rede e pode reduzir disponibilidade de escrita quando a standby ou o link interzonal falha. A aplicação deve privilegiar não confirmar operações financeiras cuja durabilidade exigida não foi atendida.
- O RPO 0 vale somente para commits confirmados segundo a configuração síncrona e para um failover que promova uma standby contendo esses commits. Não é uma garantia deste repositório nem do container local.
- O RTO depende do tempo de detecção, fencing, promoção, atualização de endpoint, reconexão dos pools e validação pós-failover. Deve ser medido em drill, não inferido da configuração.
- O read model pode estar atrasado em relação à escrita. A API informa sua atualidade; ela não deve fazer fallback síncrono para o banco de escrita sem uma decisão de consistência e disponibilidade.
- Uma réplica no mesmo host não atende a perda de host/zona. Dois bancos Docker locais continuam sendo um ambiente de demonstração.

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
