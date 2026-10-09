# Pendências e limitações conhecidas

Tudo abaixo é **honesto e deliberado**: são coisas que não foram feitas, não puderam ser validadas aqui ou dependem de uma decisão do dono.

## Decisões e ações que dependem do dono

| # | Pendência | Por quê |
|---|---|---|
| D1 | Confirmar o **nome do detentor dos direitos** na licença MIT | está como “Tersus contributors” |
| D2 | **Assinatura de código** (certificado) para o EXE e o instalador | hoje não são assinados e não têm reputação no SmartScreen; o Windows pode avisar |
| D3 | **Autorizar** (ou não) a publicação de um release no GitHub | nenhum release público foi criado; os binários estão como *artefatos* da CI |
| D4 | Teste em **máquina física** Windows 10 e Windows 11, de preferência primeiro numa VM local | só máquinas virtuais descartáveis da CI foram usadas |
| D5 | Fornecer o **ZIP original do 0.2.0** em disco para reconciliação | ver `ORIGEM_E_LIMITACOES.md` |
| D6 | Decidir se o Tersus pode **iniciar o desinstalador oficial** de um programa a partir da lista | hoje só abre *Aplicativos instalados* do Windows |
| D7 | Autorizar **novas categorias de limpeza** (cache de miniaturas, relatórios de erro, etc.) | exigem autorização, sandbox e testes novos antes de qualquer código |

## Validações que ainda faltam

- **Revisão visual das capturas de tela** geradas pela CI (artefato `evidencias-interface-e-instalador`): passaram nas verificações automáticas, mas nenhuma pessoa as olhou ainda.
- Windows 10 (a CI usa Windows Server 2025, que compartilha a base do Windows 11 24H2).
- **Reinicialização** depois da instalação/desinstalação (a CI prova a ausência de qualquer mecanismo de execução automática, mas não reinicia a máquina).
- Leitor de tela (Narrator/NVDA), tema real de alto contraste do Windows, escalas de DPI 125/150/200 % em monitores reais, Windows em outros idiomas.
- Pastas de rede, discos externos e arquivos só-na-nuvem **reais** (simulados por atributos nos testes).
- Convivência com antivírus de terceiros e com o SmartScreen para um executável não assinado.
- Atualização por cima de uma versão antiga (só existe uma versão ainda).

## Funcionalidades ainda não implementadas

| Prioridade | Funcionalidade | Observação |
|---|---|---|
| Alta | Comparar planos de limpeza lado a lado | previsto no documento 03 |
| Alta | Atribuir crescimento do disco a aplicativos no histórico | só quando for comprovável |
| Média | Atalho de desinstalação oficial por programa | depende de D6 |
| Média | Detecção de **resíduos** de programas desinstalados | módulo de pesquisa; só com marcação de confiança, revisão manual e **nenhuma** remoção automática |
| Média | Suporte a TEMP **realocado** (fora do perfil) | hoje a limpeza é desativada nesse caso, por segurança |
| Média | Exportar relatório da análise (CSV/HTML) | somente leitura |
| Baixa | Similaridade de mídia (fotos/vídeos) nos duplicados | opcional, depois |
| Baixa | “Uso real” de aplicativos | só se existir fonte local legítima e precisa; senão continua “não disponível” |
| Baixa | Verificação **manual** de atualizações com versões assinadas | depende de D2 e D3 |
| Baixa | Build ARM64, ReadyToRun e redução de tamanho do EXE | hoje o EXE é autossuficiente e comprimido (59 MB); um build dependente do runtime seria bem menor, mas exigiria o .NET Desktop Runtime instalado |
| Baixa | Varredura mais rápida em disco **frio** | medido: 48,9 s para 170 mil arquivos numa VM (1,4 s com cache quente); 2 e 4 threads empataram, então o gargalo é o disco. Ler a MFT diretamente exigiria privilégio de administrador e foi descartado |
| Baixa | Tradução para inglês | a interface é só em português do Brasil |

## Limitações técnicas conhecidas

- Os tamanhos são **lógicos**: compressão NTFS, arquivos esparsos e hardlinks fazem o uso físico diferir. Isso está sempre rotulado.
- Mover para a Lixeira **não libera espaço** até o usuário esvaziá-la; o Tersus nunca a esvazia.
- O inventário de duplicados é limitado (80 mil arquivos inventariados, 3 mil com SHA-256 completo, 20 GiB lidos); quando o limite é atingido, os grupos aparecem como *não verificados*.
- A lista de candidatos da limpeza é limitada a 50 mil arquivos por busca.
- O executável autossuficiente de arquivo único extrai bibliotecas nativas do WPF na primeira execução, em uma pasta temporária do .NET (`%TEMP%\.net`); esses arquivos não são `.tmp`/`.temp` e não são tocados pela limpeza.
- A linha de comando tem opções internas de teste (`--smoke-test ...`); elas existem só para a CI, usam pasta de dados própria e não conseguem confirmar uma limpeza fora de uma pasta de teste informada.
