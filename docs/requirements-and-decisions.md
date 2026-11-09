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

## Segurança e operação assumidas

- JWT será validado por emissor confiável; autorização seguirá o titular da conta e políticas/perfis necessários. A chave de desenvolvimento não é reutilizável fora do ambiente local.
- Segredos de ambiente real vêm de um secret manager, com rotação; dados financeiros e identificadores pessoais não aparecem em logs.
- TLS protege tráfego. Criptografia em repouso é responsabilidade do banco/armazenamento gerenciado e das chaves administradas.
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
