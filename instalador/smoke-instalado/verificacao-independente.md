### Teste de fumaça: `Tersus.exe`

| Verificação independente | Resultado | Detalhe |
|---|---|---|
| a pasta sem permissão foi preparada (ACL de negação aplicada) | ok |  |
| um arquivo foi colocado na Lixeira antes da execução (para provar que ela não é esvaziada) | ok |  |
| código de saída do teste de fumaça é 0 | ok | exit=0 em 10 s |
| o programa não abriu conexões nem portas de rede (amostragem a cada 0,3 s) | ok |  |
| o relatório do teste de fumaça foi gerado | ok |  |
| o relatório diz APROVADO | ok |  |
| nenhum erro de binding | ok |  |
| nenhuma exceção não tratada | ok |  |
| capturas de tela foram geradas | ok | 22 arquivo(s) |
| as capturas não estão vazias | ok |  |
| isca intacta: recente.tmp | ok |  |
| isca intacta: modificado-recente.tmp | ok |  |
| isca intacta: criado-recente.tmp | ok |  |
| isca intacta: quase-14-dias.tmp | ok |  |
| isca intacta: documento.txt | ok |  |
| isca intacta: foto.jpg | ok |  |
| isca intacta: relatorio-antigo.docx | ok |  |
| isca intacta: velho.tmp.exe | ok |  |
| isca intacta: sem-extensao | ok |  |
| isca intacta: grande.tmp | ok |  |
| isca intacta: somente-leitura.tmp | ok |  |
| isca intacta: sistema.tmp | ok |  |
| isca intacta: hardlink-a.tmp | ok |  |
| isca intacta: hardlink-b.tmp | ok |  |
| isca intacta: em-uso.tmp | ok |  |
| isca intacta: velho-negado.tmp | ok |  |
| arquivo fora da pasta (alvo de link) intacto: fora-1.tmp | ok |  |
| arquivo fora da pasta (alvo de link) intacto: fora-2.tmp | ok |  |
| a pasta chamada vazia.tmp continua existindo | ok |  |
| o link continua existindo: juncao | ok |  |
| o link continua existindo: link-para-fora.tmp | ok |  |
| saiu da pasta: velho-1.tmp | ok |  |
| saiu da pasta: velho-2.tmp | ok |  |
| saiu da pasta: velho-3.temp | ok |  |
| saiu da pasta: aninhado-1.tmp | ok |  |
| saiu da pasta: aninhado-2.temp | ok |  |
| saiu da pasta: acentuação-ü-日本語.tmp | ok |  |
| a Lixeira do usuário pôde ser lida para a verificação independente | ok | 16 registro(s) em Lixeira do usuário |
| está na Lixeira (registro $I e conteúdo $R): velho-1.tmp | ok |  |
| está na Lixeira (registro $I e conteúdo $R): velho-2.tmp | ok |  |
| está na Lixeira (registro $I e conteúdo $R): velho-3.temp | ok |  |
| está na Lixeira (registro $I e conteúdo $R): aninhado-1.tmp | ok |  |
| está na Lixeira (registro $I e conteúdo $R): aninhado-2.temp | ok |  |
| está na Lixeira (registro $I e conteúdo $R): acentuação-ü-日本語.tmp | ok |  |
| isca NÃO está na Lixeira: recente.tmp | ok |  |
| isca NÃO está na Lixeira: modificado-recente.tmp | ok |  |
| isca NÃO está na Lixeira: criado-recente.tmp | ok |  |
| isca NÃO está na Lixeira: quase-14-dias.tmp | ok |  |
| isca NÃO está na Lixeira: documento.txt | ok |  |
| isca NÃO está na Lixeira: foto.jpg | ok |  |
| isca NÃO está na Lixeira: relatorio-antigo.docx | ok |  |
| isca NÃO está na Lixeira: velho.tmp.exe | ok |  |
| isca NÃO está na Lixeira: sem-extensao | ok |  |
| isca NÃO está na Lixeira: grande.tmp | ok |  |
| isca NÃO está na Lixeira: somente-leitura.tmp | ok |  |
| isca NÃO está na Lixeira: sistema.tmp | ok |  |
| isca NÃO está na Lixeira: hardlink-a.tmp | ok |  |
| isca NÃO está na Lixeira: hardlink-b.tmp | ok |  |
| isca NÃO está na Lixeira: em-uso.tmp | ok |  |
| isca NÃO está na Lixeira: velho-negado.tmp | ok |  |
| a Lixeira NÃO foi esvaziada: o item que já estava lá continua | ok |  |
| todos os itens que estavam na Lixeira antes continuam nela | ok | 9 de 9 |
| o registro da limpeza foi gravado | ok |  |
| o registro lista os arquivos movidos | ok | 6 linha(s) |
| o registro diz que o arquivo em uso foi ignorado (InUse) | ok |  |
| o modo de teste não tocou na pasta de dados real (%LOCALAPPDATA%\Tersus) | ok |  |
