# Escopo: o que o Tersus 0.3.0 entrega, parcialmente entrega e ainda não entrega

Referência: documento *03 — Visão do produto, lacunas e repasse de implementação* (Drive). Estados: **Feito** = implementado e coberto por testes/CI ·
**Parcial** = implementado com limites declarados · **Pendente** = não implementado (ver `PENDENCIAS.md`).

| Funcionalidade (documento 03) | Estado no 0.2.0 (segundo o documento) | Estado no 0.3.0 | Observações |
|---|---|---|---|
| GUI desktop nativa WPF, em português | codificada, sem validação em Windows | **Feito** | 9 telas (Início, Visão geral, Maiores arquivos, Duplicados, Limpeza, Aplicativos, Histórico, Recomendações, Segurança e sobre). Aberta e percorrida por um teste de fumaça em Windows na CI, que falha em erro de *binding*, exceção não tratada ou expectativa quebrada. Acessibilidade: teclado, atalhos, zoom, nomes de automação, cor nunca é o único sinal, paleta de alto contraste (ver limites em `PENDENCIAS.md`). |
| Analisar pasta/unidade e maiores arquivos | implementação inicial | **Feito** | Varredura paralela com memória limitada, cancelável, sem seguir links; hierarquia navegável com mapa de blocos (treemap), lista ordenada, busca/filtro por tipo e tamanho; tamanho **lógico** sempre rotulado, tamanho **alocado** mostrado por arquivo; arquivos só-na-nuvem contados à parte; diferença para o uso informado pelo Windows é explicada. |
| Explicar arquivos e riscos | regras básicas | **Feito** | Base local de regras revisável (`FileExplainer`) com origem, consequência, recomendação, nível de risco **e nível de confiança** (alta/média/baixa); o desconhecido é dito como desconhecido. Explicar nunca implica permissão para apagar. |
| Temporários e limpeza | só `.tmp/.temp` antigos no TEMP, confirmação, Lixeira | **Feito** | Mesma superfície destrutiva (nenhuma categoria nova). Fluxo em 3 passos: buscar → simular → confirmar. Revalidação por arquivo sob trava. Prova de chegada à Lixeira. Ver `SEGURANCA.md`. |
| Simulação, seleção e meta opcional | base implementada | **Parcial** | Modos *rápido*, *personalizado* e *por objetivo* (escolhe os mais antigos primeiro; diz o quanto falta se não alcançar, sem afrouxar regras). Mostra tamanho lógico estimado e, depois, a variação **medida** do espaço livre, deixando claro que a Lixeira não libera espaço. **Pendente:** comparar dois planos lado a lado. |
| Arquivos duplicados | SHA-256 limitado, só relatório | **Feito** | Comparação progressiva (tamanho → amostra início/fim de 64 KB → deduplicação de hardlinks → SHA-256 completo dentro de um orçamento). Grupos não verificados por completo são rotulados. **Nunca** apaga nem move. **Pendente (opcional):** similaridade de mídia. |
| Aplicativos instalados | inventário simples | **Parcial** | Nome, editora, versão, data, local e tamanho **informado pelo instalador** (rotulado como tal), com busca e ordenação; abre o painel oficial *Aplicativos instalados* do Windows. **Não** desinstala nada e **não** mexe no registro. **Pendente:** atalho de desinstalação por programa (decisão do dono, pois exigiria iniciar o desinstalador do programa). |
| Uso real de aplicativos | NÃO implementado | **Pendente (por decisão)** | Não se infere “último uso” de datas de arquivos. A tela não mostra esse dado. |
| Resíduos de programas desinstalados | NÃO implementado | **Pendente** | Sem detecção e sem remoção. |
| Histórico do crescimento de disco | resumo simples por raiz | **Parcial** | Até 24 resumos locais por análise **completa** (totais e pastas de primeiro nível, sem nomes de arquivos), comparação com a análise anterior da mesma raiz, barras de evolução, apagar histórico com confirmação. **Pendente:** atribuir variação a aplicativos. |
| Windows, Chrome, PWAs, Blender | só informações/recomendações | **Feito (somente leitura)** | Recomendações explicam hibernação, paginação, `Windows.old`, Lixeira, Downloads, nuvem e navegadores/Blender detectados; nada é alterado, e os dados dessas áreas são classificados como **protegidos**. |
| Instalação | arquivo de projeto Inno Setup | **Feito** | Instalador por usuário, sem elevação, sem inicialização automática; EXE autossuficiente de arquivo único. Instalar/usar/desinstalar verificados em máquina Windows virtual limpa na CI. |
| Atualizações | NÃO implementado | **Pendente (por decisão)** | Somente se o dono autorizar no futuro, e sempre manual, com versões assinadas. |

## Três fluxos de uso solicitados

| Fluxo | Como funciona no 0.3.0 |
|---|---|
| **Limpeza rápida** | “Procurar temporários antigos” → todos os elegíveis já marcados → *Simular* → conferir → *Mover para a Lixeira…* → janela de confirmação. |
| **Limpeza personalizada** | O usuário marca/desmarca cada arquivo (cada linha mostra tamanho, idade e local); qualquer mudança descarta a simulação. |
| **Limpeza por objetivo** | O usuário digita o objetivo (MB/GB); o Tersus seleciona os mais antigos entre os **já aprovados** e informa se não chega lá, sem relaxar nenhuma regra. |

## Critérios de aceite do documento 01, e onde cada um é verificado

| Critério | Verificação |
|---|---|
| 1. Suite inteira de testes passa | job “Testes no Windows” e “Testes portáteis no Linux” |
| 2. `Tersus.exe` abre como desktop real em Windows x64 | teste de fumaça com o EXE publicado e com o EXE instalado (runner Windows Server 2025; **Windows 10 não foi testado**) |
| 3. Guia entre unidade/pasta, visão geral, grandes, duplicados, limpeza, apps e recomendações | o teste de fumaça visita cada tela e tira capturas |
| 4. Memória limitada, UI responsiva, cancelamento | testes do scanner (limite de nós retidos, cancelamento) + etapas de cancelamento e de árvore grande do teste de fumaça (ver relatório de build) |
| 5. Nenhuma alteração durante a simulação | testes de unidade + o teste de fumaça confere que todos os arquivos continuam lá após simular |
| 6. Lixeira só com confirmação explícita, travas e revalidação | `CleanupExecutorTests`, `WindowsCleanupTests`, janela de confirmação verificada na CI, verificação independente dos registros `$I`/`$R` |
| 7. Instalador sem elevação; atalhos; sem alterar dados pessoais | `Test-Installer.ps1` (ver `TESTES.md`) |
| 8. Sem internet, serviço, tarefa agendada ou coleta | auditoria estática + comparação antes/depois do instalador + verificação de conexões do processo |
| 9. Release só com assinatura ou avisos claros | nenhum release público foi criado; o instalador e o app dizem que **não são assinados** |
