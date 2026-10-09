# Changelog

## 0.3.0 (candidata, não assinada)

Reconstrução a partir da especificação do 0.2.0 (ver `docs/ORIGEM_E_LIMITACOES.md`) e primeira versão compilada, testada e empacotada em Windows.

### Segurança
- Verificação por **identificador aberto**: caminho final, identidade do arquivo e contagem de hardlinks; trava que impede escritores e troca do arquivo enquanto ele é revalidado.
- Confirmação **amarrada ao plano** (id + impressão digital SHA-256); qualquer divergência não executa nada.
- Envio à Lixeira com verificação prévia da configuração da Lixeira, arquivo de autoteste e **prova de chegada** pelos registros `$I`/`$R`; lote interrompido se um arquivo some sem prova.
- Auditoria estática do código-fonte (APIs proibidas, lista exata de funções nativas, constantes da política) com testes do próprio auditor.
- Teste de fuzz da política (100 mil casos). Ele achou duas brechas reais, já corrigidas: contagem de links igual a zero e atributo de pasta sem o indicador de pasta.

### Produto
- Interface WPF com 9 telas, mapa de blocos (treemap), explicador com nível de confiança, duplicados progressivos, histórico comparativo, recomendações somente leitura.
- Limpeza em 3 passos (buscar, simular, confirmar), modos rápido/personalizado/por objetivo, relatório com espaço livre **medido**.
- Acessibilidade: teclado, atalhos Ctrl+1..9, zoom, cor nunca é o único sinal, paleta de alto contraste.
- Instalador por usuário (Inno Setup 6) e EXE autossuficiente de arquivo único.

### Defeitos encontrados pela validação e corrigidos
Ver a tabela completa em `docs/RELATORIO_DE_VALIDACAO.md`. Em resumo: duas brechas da política achadas pelo fuzz (links = 0; atributo de pasta), um conflito de chave de recurso (`WarnButton`) que só aparece com a interface rodando, falhas dos próprios roteiros de teste (modo estrito, unidades diferentes, arquivo em uso) e um passo do instalador.

### Pipeline (GitHub Actions, Windows)
- Testes de Core, auditoria e integração em Windows real; build com `-warnaserror`; publicação; teste de fumaça da interface real com verificação independente da Lixeira; instalação, uso e desinstalação silenciosas; SHA-256 e artefatos.
