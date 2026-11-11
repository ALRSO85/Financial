# Requisitos e decisões

Este documento registra como o projeto responde à devolutiva da avaliação. Os pontos descritos pela empresa ficam separados das premissas usadas para tornar o exercício verificável. SLA, SLO, volume, latência, RTO e RPO não foram informados no desafio; os valores abaixo são alvos de ensaio, não compromissos aprovados.

## Escopo funcional

| ID | Necessidade | Decisão adotada |
| --- | --- | --- |
| RF-01 | Registrar créditos e débitos | Cada lançamento é imutável e tem uma chave de idempotência por conta. |
| RF-02 | Consultar saldo em um instante | O histórico considera os lançamentos efetivados até o instante informado. |
| RF-03 | Consolidar o saldo do titular | Somar as contas do titular somente quando usarem a mesma moeda. O exercício assume BRL. |
| RF-04 | Evitar inconsistência financeira | Lançamento, saldo contábil, auditoria e evento da outbox são confirmados na mesma transação de escrita. |
| RF-05 | Restringir acesso | O titular só consulta ou movimenta contas associadas à sua identidade autenticada. |

## Premissas do exercício

O modelo usa BRL em centavos inteiros, sem conversão de moeda. O saldo retornado é contábil; reservas, saldo disponível, crédito, liquidação externa, transferências e pagamentos ficam fora do escopo. Lançamentos confirmados não são alterados nem removidos. Uma correção cria um lançamento compensatório ligado ao original. A API define o instante do lançamento e não aceita retroatividade. O débito não pode deixar o saldo contábil negativo. A outbox retém eventos para replay, mas ainda não há política de arquivamento ou expurgo. Essas premissas precisam de validação do produto antes de serem tratadas como regras da empresa.

## Consistência e indisponibilidade

| Situação | Comportamento esperado |
| --- | --- |
| Movimentação confirmada | Lançamento, saldo e outbox ficam gravados no mesmo commit do banco de escrita. |
| Reenvio com a mesma chave e os mesmos dados | Retorna o resultado já confirmado. Se a chave for reutilizada com dados diferentes, responde conflito. A constraint do banco protege a unicidade entre processos. |
| Consulta consolidada | Usa uma projeção separada e eventualmente consistente. A resposta informa `dataAsOf` para mostrar até quando os dados foram aplicados. |
| Projetor atrasado | A hipótese inicial é atualizar em até 30 s e sinalizar degradação após 5 min. Esses limites aguardam validação do negócio. |
| API ou banco de escrita indisponível | Não confirmar novos lançamentos. A consulta pode servir a última projeção e informar que ela está atrasada. |
| Banco de leitura indisponível | A escrita continua aceitando operações e acumulando eventos na outbox; as consultas não prometem disponibilidade enquanto o banco de leitura estiver fora. |
| Entrega repetida de evento | O projetor é idempotente e verifica versão/checkpoint para não duplicar nem regredir a projeção. |

## Metas operacionais para ensaio

| Indicador | Alvo inicial | Observação |
| --- | --- | --- |
| Disponibilidade mensal das APIs | 99,9% para escrita e consulta | Exige definir janela de manutenção e medir em infraestrutura redundante. |
| Latência de escrita | p95 ≤ 300 ms; p99 ≤ 1 s | Medida na API, sem latência do cliente. |
| Latência de consulta | p95 e p99 ≤ 500 ms | Medida para consultas por conta e consolidadas sob carga acordada. |
| Carga de referência | 100 operações/s sustentadas; pico de 500 operações/s por 5 min | Premissa de teste, não volume informado pela empresa. |
| Retenção do ledger | Durante toda a vida da conta | Requer validação legal e de negócio; não aplicar expurgo genérico a lançamentos financeiros. |
| RPO de lançamentos confirmados | 0 | Só pode ser afirmado após testar a configuração de alta disponibilidade do banco. |
| RTO de escrita e consulta | 30 min e 15 min | Metas propostas, a medir em exercícios de failover ou restore. |
| Recuperação da projeção | Até 15 min para 1 h de backlog | Hipótese inicial, ainda precisa ser medida com volume de referência. |

## Decisões de arquitetura

| Decisão | Motivo e limite |
| --- | --- |
| PostgreSQL como ledger | Transações ACID, constraints e locks por conta protegem gravações concorrentes entre processos. O custo é operar e recuperar o banco; backup e restore precisam de ensaio. |
| API e banco de leitura separados | Permitem servir a última projeção sem depender do processo nem da conexão de escrita. A contrapartida é consistência eventual e necessidade de monitorar e reconstruir a projeção. |
| Outbox transacional | Evita gravar o lançamento sem registrar o evento que alimenta a leitura. O worker pode repetir eventos com segurança. Um broker foi deixado para depois porque acrescentaria operação sem eliminar retry e idempotência. |
| Alta disponibilidade | A proposta usa standby síncrona e failover com fencing em domínios distintos. O banco de leitura também precisa de HA própria e pode ser reconstruído da outbox. O sandbox tem dois bancos no mesmo servidor e não demonstra essa topologia. Consulte a [ADR-0001](decisions/adr-0001-postgresql-high-availability.md). |
| JSON | Permanece como adapter de testes, não como persistência concorrente do ledger. |
| Metas numéricas | Permanecem hipóteses até que negócio e operação definam orçamento, carga e domínio de falha. |

## Por que essas escolhas

PostgreSQL substitui o arquivo JSON porque fornece transações, unicidade e locks coordenados pelo banco. O arquivo exigia carregar e regravar o estado inteiro e não coordenava instâncias diferentes.

A leitura separada reduz a dependência do serviço transacional, embora possa apresentar dados atrasados. A outbox mantém o evento no mesmo commit do lançamento e evita a janela em que o dado financeiro foi gravado, mas a publicação falhou. Um broker pode ser avaliado se houver vários consumidores independentes ou necessidade maior de vazão.

Os modos `write` e `read` compartilham contratos e autenticação, mas rodam em processos separados. Isso isola falhas de processo; não protege contra a perda do host. O JSON não é usado como ledger operacional. Nenhuma das metas operacionais deve ser chamada de SLA empresarial antes de ser aprovada e medida.

## Rastreabilidade da devolutiva

| Ponto da avaliação | Evidência no projeto | Limite atual |
| --- | --- | --- |
| Requisitos e documentação | Requisitos, premissas e metas estão neste arquivo; desenho em [`architecture.md`](architecture.md), operação em [`runbook.md`](runbook.md) e HA na ADR-0001. | Metas de SLA/SLO, RTO/RPO e carga não foram aprovadas pela empresa. |
| Disponibilidade da consolidação | O teste `Read_api_stays_available_during_write_process_outage_and_catches_up_after_recovery` para um processo real de escrita, consulta 1.250 centavos e observa 1.750 após recuperar e projetar o evento pendente. | Não testa queda do PostgreSQL, do host ou de uma zona. Os bancos de sandbox compartilham servidor. |
| Testes | A suíte cobre regras, autorização, idempotência, concorrência, replay e projeção. O runbook registra ensaios de carga e backup/restore. | Não há evidência de split brain, failover físico ou carga durante failover. |
| Observabilidade | Health checks, `dataAsOf`, métricas, traces, logs correlacionados e alertas locais estão descritos em [`architecture.md`](architecture.md) e [`runbook.md`](runbook.md). | Não há receiver externo nem política de retenção operacional de produção. |
| Segurança | Há escopos, isolamento por titular, limites de requisição, auditoria e papéis PostgreSQL separados no sandbox. | Secret manager, rotação de produção, TLS e cifragem em repouso dependem da plataforma. O backup usado na migração era local e sem cifragem. |
| Domínio e consistência | Moeda, saldo contábil, compensações e consistência eventual estão declarados como premissas. | Produto precisa confirmar moeda, estornos, reservas, retroatividade e atraso tolerado. |
| Persistência e recuperação | PostgreSQL é a fonte autoritativa; a projeção pode ser refeita da outbox. Restore local foi ensaiado com cópia cifrada temporária e cópia em claro explicitamente autorizada para sandbox. | O sandbox continua permitindo backup em claro até configurar `age`. Não há cópia externa ou PITR. |

## Segurança e operação

Em produção, a API deve validar JWT de um emissor confiável, autorizar por titular e escopo e buscar credenciais em um secret manager com política de rotação. O sandbox usa uma chave JWT local aleatória, guardada fora do repositório; a API recusa chave simétrica fora de `Development`. As conexões fora de `Development` exigem TLS com validação de certificado. Criptografia em repouso depende do banco ou armazenamento escolhido.

Logs e métricas não devem expor identificadores de clientes ou valores financeiros. Health checks, traces, métricas e logs correlacionados devem ajudar a identificar latência, erro e atraso da projeção. O backup só deve ser considerado válido depois de restaurado e reconciliado. O runbook descreve deploy, rollback, recuperação e troubleshooting.

## Critérios de aceite

| Critério | Verificação |
| --- | --- |
| Lançamento, saldo e outbox são atômicos | Teste de falha antes do commit e conferência das três estruturas. |
| Concorrência não aprova débitos além do saldo nem duplica chave | Testes com conexões e processos independentes. |
| Consulta continua durante a parada da API de escrita | Teste de disponibilidade com `dataAsOf` e catch-up após recuperação. |
| Replay não duplica nem regride a projeção | Testes de repetição e eventos fora de ordem. |
| Degradação é visível | Health checks e métricas expõem estado e idade do backlog sem dados sensíveis. |
| Restore funciona | Restaurar em ambiente isolado e reconciliar saldo, lançamentos, outbox e migrations. |
| HA de produção está comprovada | Medir RPO/RTO e reconciliar após failover em domínios de falha independentes. A topologia local não serve como essa evidência. |
