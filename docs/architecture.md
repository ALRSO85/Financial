# Arquitetura e garantias

## Escopo

A solução registra um lançamento por chamada e consulta o saldo contábil de uma conta. Ela não movimenta dinheiro entre contas e não integra com sistemas de pagamento. Isso mantém o escopo do desafio focado em consistência do ledger.

```text
Cliente
  │ HTTP
  ▼
Ledger.Api (rotas e validação)
  │
  ▼
LedgerService (regras e gravação local)
  └── ledger.json
      ├── Accounts: saldo corrente
      └── Entries: histórico imutável + chave idempotente
```

## Fluxo de lançamento

1. Validar tipo, valor em centavos, descrição e chave idempotente.
2. Procurar chave já usada na mesma conta. Se os parâmetros forem iguais, devolver a transação anterior; se diferirem, responder conflito.
3. Ler a conta e calcular novo saldo com aritmética `checked`. Débito que resulte em saldo negativo é recusado.
4. Atualizar o saldo e anexar a entrada ao estado carregado.
5. Regravar o estado completo em um arquivo temporário e substituir o arquivo ativo antes de devolver sucesso.

Um `SemaphoreSlim` serializa chamadas recebidas pela mesma instância da API. Isso protege a leitura-modificação-gravação dentro do processo, mas não coordena processos concorrentes. O arquivo inteiro é reescrito a cada alteração, então o custo aumenta conforme o ledger cresce. Essa implementação serve ao exercício local e não demonstra throughput, isolamento transacional ou recuperação de produção.

## Saldo histórico

O saldo atual usa a projeção mantida na conta. Para `at`, o serviço agrega os valores assinados de entradas com `OccurredAt <= at`. `System.Text.Json` serializa os instantes ISO-8601; comparações são feitas como `DateTimeOffset` em memória. `OccurredAt` é o instante de registro, não substitui uma eventual data-valor de negócio. Se operações retroativas forem permitidas, a regra de saldo corrente e a definição temporal precisam ser revistas.

## Falhas e recuperação

- A serialização e substituição por arquivo temporário reduz o risco de truncar o JSON se uma gravação falhar, mas não substitui garantias transacionais e de durabilidade de um banco.
- Reenvio após timeout usa a mesma chave para determinar se a gravação anterior aconteceu.
- Uma inconsistência entre projeção e ledger deve ser detectável por reconciliação; a projeção pode ser reconstruída a partir das entradas.
- Entradas não são editadas nem apagadas. Ajustes são lançamentos opostos com referência de auditoria.
- Em ambiente produtivo, migrations são aplicadas como etapa controlada de deploy; backup, restauração e reconciliação precisam de exercícios regulares.

## Próximos passos para operação bancária

Antes do tráfego real, fechar requisitos para moeda/escala, limites e reservas, saldo disponível e contábil, estornos, lançamentos atrasados, ordenação estável e retenção. Migrar para banco relacional gerenciado, adotar migrations, autenticação/autorização, criptografia em trânsito e repouso, gestão de segredos, auditoria, observabilidade sem dados pessoais, rate limiting, proteção de abuso e runbooks de incidente. Definir metas de latência e disponibilidade com o negócio antes de escolher particionamento, cache ou arquitetura distribuída.
