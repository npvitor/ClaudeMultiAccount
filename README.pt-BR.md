# Claude Multi-Account

**Use várias contas do Claude Desktop ao mesmo tempo no Windows**: trabalho e pessoal, dois clientes, o que você precisar. Cada conta tem o próprio login, configurações, histórico e sessões do Claude Code, e todas podem ficar abertas juntas.

[Read in English](README.md)

> Projeto não oficial, feito pela comunidade. Sem vínculo com a Anthropic.

## Por quê

O Claude Desktop só faz login em uma conta por usuário do Windows e só abre uma janela por usuário. O jeito confiável de rodar uma segunda conta é rodá-la como um **segundo usuário do Windows**. Fazer isso na mão significa criar o usuário, compartilhar as pastas dos projetos, instalar o Claude para ele, resolver o erro "dubious ownership" do Git e digitar a senha em "Executar como outro usuário" toda vez.

Este launcher faz tudo isso por você e deixa um botão **Abrir Claude** para cada conta.

## Recursos

- **Um clique por conta**, pela janela ou pelo menu da bandeja.
- **Usuários ocultos do Windows**: cada perfil ganha um usuário local padrão próprio. Ele não aparece na tela de login e não é administrador.
- **Nenhuma senha para decorar**: uma senha aleatória de 28 caracteres é gerada para cada perfil e guardada criptografada com o DPAPI do Windows. Só a sua conta consegue lê-la.
- **Os projetos continuam onde estão**: você escolhe quais pastas cada perfil pode ler e alterar. Pastas comuns, como `source\repos` e `C:\Git`, já vêm sugeridas.
- **O Git funciona direto**: as pastas entram como `safe.directory`, e dá para definir `user.name` e `user.email` por perfil. Os commits pessoais saem com o e-mail pessoal.
- **O Claude fica atualizado**: quando o Claude Desktop da conta principal atualiza, o perfil recebe a versão nova na próxima vez que você abrir.
- **Terminal por perfil**: uma janela do PowerShell rodando como aquela conta, para o Claude Code CLI, `git` ou `gh auth login`.
- Inicia com o Windows (opcional), fica na bandeja e roda em instância única.
- Interface em português ou inglês, conforme o idioma do Windows.

## Requisitos

- Windows 10 (2004 ou mais novo) ou Windows 11, 64 bits.
- Sua conta do Windows precisa ser **administradora**. Criar, alterar ou remover um perfil pede aprovação do UAC; abrir o Claude não pede.
- [Claude Desktop](https://claude.ai/download) instalado na conta principal.
- Opcional: Git 2.46 ou mais novo, para as entradas de `safe.directory` por pasta.

## Instalação

1. Baixe o `ClaudeMultiAccount.exe` na página de [Releases](../../releases). É um arquivo único e não precisa de .NET instalado.
2. Execute. O SmartScreen pode avisar que o app não é assinado: clique em **Mais informações → Executar assim mesmo**.

## Como usar

1. Clique em **Novo perfil**, dê um nome (por exemplo *Pessoal*), escolha a cor e as pastas de projetos que ele pode usar e, se quiser, o nome e o e-mail do Git.
2. Aprove o pedido do UAC. O app cria o usuário oculto, prepara o perfil do Windows, instala o Claude Desktop nele e configura o Git. Leva cerca de um minuto.
3. O Claude abre no novo perfil. Faça login com a outra conta; isso é feito uma única vez.
4. Depois disso é só usar **Abrir Claude** em cada cartão, ou o menu da bandeja.

O menu **⋯** de cada perfil tem:

| Opção | O que faz |
| --- | --- |
| Abrir terminal deste perfil | PowerShell rodando como aquela conta (Claude Code CLI, git, gh…) |
| Editar perfil… | Renomear, trocar a cor, as pastas e a identidade do Git |
| Reparar / atualizar Claude no perfil | Refaz a configuração se algo quebrou |
| Copiar senha do usuário do Windows | Só para resolver problemas |
| Remover perfil… | Exclui o usuário oculto e os dados dele; os arquivos dos projetos não são afetados |

## Como funciona

| Etapa | Técnica |
| --- | --- |
| Criar usuário | `NetUserAdd` (usuário padrão, senha que não expira), adicionado a *Usuários* e oculto via `Winlogon\SpecialAccounts\UserList` |
| Compartilhar pastas | `icacls <pasta> /grant *SID:(OI)(CI)M` |
| Instalar o Claude | Instalação MSIX: `Add-AppxPackage -Register` do pacote que já está na máquina, sem download. Instalação clássica: roda dentro do perfil o instalador que você escolher |
| Abrir | `CreateProcessWithLogonW` (a API do "Executar como") inicia o `claude-desktop.exe` do próprio perfil |
| Etapas de admin | O app se reabre elevado só para aquela tarefa. O pedido vai por um arquivo temporário, nunca pela linha de comando, e a senha continua criptografada com DPAPI |

Onde ficam os dados:

- `%APPDATA%\ClaudeMultiAccount\profiles.json`: seus perfis, com as senhas criptografadas com DPAPI.
- `%ProgramData%\ClaudeMultiAccount\<usuário do perfil>`: scripts temporários de configuração e logs das etapas que falharam. Cada pasta só pode ser lida pelos administradores, pela sua conta e pelo usuário daquele perfil.

## Limitações conhecidas

- O Windows pede aprovação de administrador para criar um perfil, alterar as pastas dele ou removê-lo.
- Cada perfil é um usuário real do Windows. Por isso o navegador, as credenciais do Git, o login do `gh` e o Claude Code CLI dele são separados dos seus. Essa é a ideia, mas você faz login nessas coisas uma vez por perfil. Para instalar o CLI, use o terminal do perfil: `irm https://claude.ai/install.ps1 | iex`.
- Recursos que dependem de um único componente da máquina inteira, como o serviço de VM do Cowork, podem funcionar em uma conta por vez.
- Por padrão, os usuários dos perfis são usuários padrão. Quando o Claude de um perfil roda um comando que altera a máquina inteira, como `dotnet workload install`, instalar um SDK ou confiar num certificado para a máquina toda, o Windows mostra um pedido do UAC pedindo a senha de um administrador. Você pode clicar em **Não** e rodar esse comando uma vez na sua conta principal, num terminal como administrador, ou marcar **Tornar este usuário administrador do Windows** no perfil (também em **Editar perfil**). Num perfil administrador o Claude continua rodando sem privilégios; esses comandos passam a pedir só um **Sim** em vez da senha. Ative só em perfis cujos projetos você confia.
- As notificações do Claude de um perfil podem não aparecer, porque o Windows mostra notificações do usuário logado.
- O pacote do Claude inclui um serviço do Windows, e por isso o Windows pode recusar instalá-lo para um usuário padrão. Quando isso acontece, o app pede aprovação de administrador uma vez e libera (provisiona) o pacote para todos os usuários da máquina; qualquer usuário do Windows criado depois também recebe o Claude Desktop.

## Solução de problemas

- **"O serviço Logon Secundário está desativado"**: abra o `services.msc`, coloque **Logon Secundário** em *Manual* e inicie o serviço.
- **Uma etapa da configuração falhou**: leia o log em `%ProgramData%\ClaudeMultiAccount\<usuário do perfil>`, corrija a causa e use **⋯ → Reparar**.
- **O Git diz "dubious ownership"**: use **⋯ → Editar perfil → Salvar** para adicionar a pasta de novo, e confira se o Git é 2.46 ou mais novo.
- **Desinstalar**: remova todos os perfis pelo app primeiro, o que exclui os usuários ocultos. Depois apague o exe e a pasta `%APPDATA%\ClaudeMultiAccount`.

## Compilar o código

```bash
dotnet build src/ClaudeMultiAccount/ClaudeMultiAccount.csproj -c Release
```

Versão de release em arquivo único:

```bash
dotnet publish src/ClaudeMultiAccount/ClaudeMultiAccount.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

Precisa do SDK do .NET 10. Ao enviar uma tag `v*`, o workflow incluído compila e publica um release no GitHub.

## Testes

```bash
dotnet test --project tests/ClaudeMultiAccount.Tests
```

O [TESTING.md](TESTING.md) explica os testes de integração que precisam de administrador e traz o roteiro manual para antes de um release.

## Licença

[MIT](LICENSE)
