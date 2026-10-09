# Baixar e instalar o Tersus

## Onde baixar

A página de download é a de **Releases** do repositório: <https://github.com/Josperdias/Tersus/releases>.
Cada versão traz o **instalador**, a **versão portátil**, os códigos **SHA-256** e o **relatório de testes** daquela versão.

> Enquanto o programa não for assinado digitalmente (decisão D2 em [`PENDENCIAS.md`](PENDENCIAS.md)), as versões saem marcadas como **pré-lançamento** e com o aviso de “não assinado”.

## Requisitos

- **Windows de 64 bits (x64).** Feito para Windows 10 e 11, mas **testado apenas** no Windows Server 2025 (máquina virtual descartável do GitHub). ARM64 ainda não é suportado.
- **Não** precisa instalar o .NET, **não** precisa de internet para usar e **não** pede senha de administrador.
- Espaço: cerca de 60 MB.

## Passo a passo

1. **Baixe** `Tersus-Setup-<versão>-x64.exe` (instalador, recomendado) ou `Tersus.exe` (portátil) e o `SHA256SUMS.txt`.
2. **Confira o arquivo.** No PowerShell, na pasta do download:

   ```powershell
   Get-FileHash .\Tersus-Setup-0.3.0-x64.exe -Algorithm SHA256
   ```

   O resultado tem de ser idêntico ao do `SHA256SUMS.txt` (e ao que aparece na página do release). Se for diferente, não abra o arquivo e baixe de novo.
3. **Abra o instalador.** O Windows (SmartScreen) deve mostrar **“O Windows protegeu seu computador”**, porque o programa ainda não é assinado.
   Clique em **Mais informações → Executar assim mesmo** (somente se o SHA-256 bateu).
4. **Siga o assistente** (em português). O programa é instalado só para o seu usuário, em `%LOCALAPPDATA%\Programs\Tersus`, com atalho no Menu Iniciar. Não inicia junto com o Windows, não cria serviços nem tarefas agendadas.
5. **Primeiro uso, com segurança.** Todas as telas, menos a **Limpeza**, só leem dados. Na Limpeza: *Procurar → Simular → Confirmar*; “Cancelar” é o botão padrão; os arquivos vão para a **Lixeira** (dá para restaurar), e o Tersus nunca esvazia a Lixeira.
   Teste primeiro numa máquina virtual antes de usar no computador de trabalho.

### Atualizar e desinstalar

- **Atualizar:** baixe o instalador da versão nova e execute. *Instalar por cima de uma versão antiga ainda não foi testado* (só existe uma versão até agora).
- **Desinstalar:** *Configurações → Aplicativos → Tersus → Desinstalar*. O histórico e os registros de limpeza ficam em `%LOCALAPPDATA%\Tersus`; apague a pasta se quiser.
- **Versão portátil:** basta apagar o `Tersus.exe` (e, se quiser, a pasta `%LOCALAPPDATA%\Tersus`).

### Se o Windows ou o antivírus reclamar

O aviso do SmartScreen é esperado para um programa novo e sem assinatura. Um antivírus de terceiros também pode estranhar (o Tersus **não foi testado** com antivírus de terceiros).
O código-fonte está neste repositório, e cada arquivo é gerado pelo GitHub Actions a partir dele; o `RELATORIO_DE_BUILD.md` do release informa o commit e a execução que o gerou.

## Para quem mantém o repositório: como a página de download é criada

1. Se for uma versão nova, atualize `VersionPrefix` em `Directory.Build.props` e o `CHANGELOG.md`.
2. Em *Actions → Tersus - Testes, EXE e instalador (Windows) → Run workflow*, escolha a branch **`main`** e marque **`publicar_release`**.
3. Por padrão o release sai como **rascunho** (`release_rascunho` marcado): só quem tem acesso de escrita o vê. Abra *Releases*, confira o texto e os arquivos e clique em **Publish release**.
   Para publicar direto, desmarque `release_rascunho`.
4. O job só roda a partir da `main`, publica **exatamente os arquivos que aquela execução compilou, testou e instalou/desinstalou**, cria a etiqueta `v<versão>` e **nunca sobrescreve** um release já publicado (um rascunho com o mesmo número é substituído).
5. **Não precisa configurar nenhum *secret***: o token automático do GitHub basta. Secrets só entrarão quando houver assinatura de código (D2).
6. Quando o programa for assinado e validado em máquina física, remova `--prerelease` do job `publicar-release` em `.github/workflows/windows-build.yml`.
