using System.Globalization;

namespace ClaudeMultiAccount.Localization;

/// <summary>UI strings. Portuguese when Windows is in Portuguese, English otherwise.</summary>
public static class S
{
    static bool Pt => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pt";
    static string T(string pt, string en) => Pt ? pt : en;

    // Main window
    public static string AppName => "Claude Multi-Account";
    public static string Subtitle => T("Use várias contas do Claude Desktop ao mesmo tempo", "Run several Claude Desktop accounts side by side");
    public static string Settings => T("Configurações", "Settings");
    public static string StartWithWindows => T("Iniciar com o Windows", "Start with Windows");
    public static string OpenLogs => T("Abrir pasta de logs", "Open logs folder");
    public static string About => T("Sobre", "About");
    public static string AboutText => T(
        "Claude Multi-Account {0}\n\nAbre várias contas do Claude Desktop ao mesmo tempo, cada uma em um usuário oculto do Windows.\n\nProjeto de código aberto (MIT) e não oficial, sem vínculo com a Anthropic.",
        "Claude Multi-Account {0}\n\nOpens several Claude Desktop accounts at once, each one in a hidden Windows user.\n\nOpen-source (MIT), unofficial project, not affiliated with Anthropic.");
    public static string Exit => T("Sair", "Exit");
    public static string ShowWindow => T("Mostrar janela", "Show window");
    public static string ClaudeNotFound => T(
        "O Claude Desktop não foi encontrado nesta conta do Windows.",
        "Claude Desktop was not found for this Windows account.");
    public static string Download => T("Baixar", "Download");
    public static string OpenClaude => T("Abrir Claude", "Open Claude");
    public static string More => T("Mais opções", "More options");
    public static string NewProfile => T("Novo perfil", "New profile");
    public static string HowItWorks => T(
        "Cada perfil roda em um usuário oculto do Windows criado só para ele. Assim, o login, as configurações e o histórico do Claude ficam separados, e todas as contas podem ficar abertas ao mesmo tempo.",
        "Each profile runs in a hidden Windows user created just for it. Claude's sign-in, settings and history stay separate, and every account can be open at the same time.");
    public static string MainName => T("Conta principal", "Main account");
    public static string MainDetail => T("Sua conta do Windows ({0})", "Your Windows account ({0})");
    public static string ProfileDetail => T("Usuário oculto do Windows: {0}", "Hidden Windows user: {0}");
    public static string ProfileAdminSuffix => T(" · administrador", " · administrator");

    public static string MenuTerminal => T("Abrir terminal deste perfil", "Open terminal for this profile");
    public static string MenuEdit => T("Editar perfil…", "Edit profile…");
    public static string MenuRepair => T("Reparar / atualizar Claude no perfil", "Repair / update Claude in profile");
    public static string MenuCopyPassword => T("Copiar senha do usuário do Windows", "Copy Windows user password");
    public static string MenuRemove => T("Remover perfil…", "Remove profile…");

    public static string StatusOpeningMain => T("Abrindo o Claude da conta principal…", "Opening Claude for the main account…");
    public static string StatusOpening => T("Abrindo o Claude de \"{0}\"…", "Opening Claude for \"{0}\"…");
    public static string StatusOpeningTerminal => T("Abrindo terminal de \"{0}\"…", "Opening terminal for \"{0}\"…");
    public static string StatusUpdatingProfile => T("Atualizando o Claude em \"{0}\" (só na primeira vez após uma atualização)…", "Updating Claude in \"{0}\" (only once after each update)…");
    public static string StatusRepairing => T("Reparando \"{0}\"…", "Repairing \"{0}\"…");
    public static string StatusRemoving => T("Removendo \"{0}\"…", "Removing \"{0}\"…");

    public static string ConfirmRemove => T(
        "Remover o perfil \"{0}\"?\n\nIsso exclui o usuário oculto do Windows \"{1}\" e os dados dele, incluindo o login do Claude desse perfil. Os arquivos dos seus projetos não são afetados.",
        "Remove profile \"{0}\"?\n\nThis deletes the hidden Windows user \"{1}\" and its data, including this profile's Claude sign-in. Your project files are not touched.");
    public static string PasswordCopied => T(
        "Senha copiada para a área de transferência.\n\nNormalmente você não precisa dela; serve para resolver problemas (por exemplo, \"Executar como outro usuário\").",
        "Password copied to the clipboard.\n\nYou normally don't need it; it's there for troubleshooting (for example \"Run as different user\").");
    public static string RepairDone => T("Pronto! O perfil foi reparado.", "Done! The profile was repaired.");
    public static string ClosedToTray => T("Continuo rodando aqui na bandeja. Clique com o botão direito para abrir um perfil.", "Still running here in the tray. Right-click to open a profile.");

    // Profile window
    public static string NewProfileTitle => T("Novo perfil", "New profile");
    public static string EditProfileTitle => T("Editar perfil", "Edit profile");
    public static string FieldName => T("Nome do perfil", "Profile name");
    public static string NamePlaceholderHint => T("Ex.: Pessoal, Trabalho, Cliente X", "E.g. Personal, Work, Client X");
    public static string WindowsUserHint => T("Usuário do Windows: {0}", "Windows user: {0}");
    public static string FieldColor => T("Cor", "Color");
    public static string FieldFolders => T("Pastas de projetos", "Project folders");
    public static string FoldersHint => T(
        "O perfil poderá ler e alterar os arquivos destas pastas (e subpastas).",
        "The profile will be able to read and change files in these folders (and subfolders).");
    public static string AddFolder => T("Adicionar pasta…", "Add folder…");
    public static string RemoveFolder => T("Remover", "Remove");
    public static string FieldGit => T("Identidade do Git (opcional)", "Git identity (optional)");
    public static string GitName => T("Nome", "Name");
    public static string GitEmail => T("E-mail", "Email");
    public static string LaunchAfter => T("Abrir o Claude deste perfil ao concluir", "Open this profile's Claude when done");
    public static string AdminOption => T("Tornar este usuário administrador do Windows", "Make this user a Windows administrator");
    public static string AdminOptionHint => T(
        "O Claude continua rodando sem privilégios. Comandos que alteram a máquina (como \"dotnet workload install\") passam a pedir só \"Sim\" em vez da senha de administrador. Ative só se você confia nos projetos usados neste perfil.",
        "Claude still runs without elevated rights. Commands that change the machine (such as \"dotnet workload install\") then ask for a simple \"Yes\" instead of an administrator password. Only enable it if you trust the projects used in this profile.");
    public static string AdminChangedRestart => T(
        "A permissão de administrador foi alterada. Se o Claude deste perfil estiver aberto, feche-o e abra de novo para a mudança valer.",
        "Administrator permission changed. If this profile's Claude is open, close it and open it again for the change to apply.");
    public static string AdminNoticeCreate => T(
        "O Windows vai pedir permissão de administrador para criar um usuário local oculto (ele não aparece na tela de login). A senha é gerada automaticamente e guardada criptografada só para você.",
        "Windows will ask for administrator permission to create a hidden local user (it won't show on the sign-in screen). Its password is generated automatically and stored encrypted for you only.");
    public static string AdminNoticeEdit => T(
        "Alterar as pastas ou a opção de administrador pede permissão de administrador.",
        "Changing folders or the administrator option asks for administrator permission.");
    public static string Cancel => T("Cancelar", "Cancel");
    public static string CreateProfile => T("Criar perfil", "Create profile");
    public static string Save => T("Salvar", "Save");
    public static string Saving => T("Salvando…", "Saving…");
    public static string Close => T("Fechar", "Close");
    public static string StepCreateUser => T(
        "Criando o usuário oculto e liberando as pastas (pastas muito grandes podem levar alguns minutos)",
        "Creating the hidden user and sharing the folders (very large folders can take a few minutes)");
    public static string StepPrepare => T("Preparando o perfil do Windows (primeiro acesso)", "Preparing the Windows profile (first sign-in)");
    public static string StepRegister => T("Instalando o Claude Desktop no perfil", "Installing Claude Desktop in the profile");
    public static string StepGit => T("Configurando o Git", "Configuring Git");
    public static string StepsHint => T(
        "Normalmente leva de 1 a 3 minutos. Pode continuar usando o computador; esta janela se atualiza sozinha.",
        "This usually takes 1 to 3 minutes. Feel free to keep working; this window updates by itself.");
    public static string StepCreateUserDetail => T(
        "Aprove o pedido de administrador do Windows. Se ele não aparecer, procure o ícone piscando na barra de tarefas.",
        "Approve the Windows administrator prompt. If you don't see it, look for a flashing icon on the taskbar.");
    public static string StepPrepareDetail => T(
        "O Windows está criando a pasta do novo usuário (costuma levar de 10 a 30 segundos).",
        "Windows is creating the new user's folder (usually 10 to 30 seconds).");
    public static string StepRegisterDetail => T(
        "Registrando o pacote do Claude para o novo usuário. Se o Windows pedir permissão de administrador de novo, aprove.",
        "Registering the Claude package for the new user. If Windows asks for administrator permission again, approve it.");

    public static string SelectInstallerTitle => T("Selecione o instalador do Claude", "Select the Claude installer");
    public static string SelectInstallerInfo => T(
        "O Claude Desktop desta conta não foi instalado pelo pacote MSIX, então não dá para reaproveitá-lo.\n\nSelecione o instalador do Claude baixado de claude.ai/download. Ele será executado dentro do perfil.",
        "This account's Claude Desktop wasn't installed from the MSIX package, so it can't be reused.\n\nSelect the Claude installer downloaded from claude.ai/download. It will run inside the profile.");
    public static string InstallerFilter => T("Instalador do Claude", "Claude installer") + "|*.exe;*.msix;*.msixbundle";

    // Errors
    public static string ErrNameRequired => T("Dê um nome ao perfil.", "Give the profile a name.");
    public static string ErrNameInvalid => T("O nome não pode ter aspas (\").", "The name can't contain quotes (\").");
    public static string ErrPartial => T(
        "O perfil foi criado, mas a configuração não terminou. Depois de resolver o problema, use ⋯ → Reparar.",
        "The profile was created but setup didn't finish. Once the problem is fixed, use ⋯ → Repair.");
    public static string ErrUnexpected => T("Ocorreu um erro inesperado:\n{0}", "Something unexpected went wrong:\n{0}");
    public static string ErrTimeout => T("A operação demorou demais e foi interrompida.", "The operation took too long and was stopped.");
    public static string ErrBadPassword => T(
        "O Windows recusou a senha do usuário deste perfil. Remova o perfil e crie de novo.",
        "Windows rejected this profile user's password. Remove the profile and create it again.");
    public static string ErrSecLogon => T(
        "O serviço \"Logon Secundário\" (seclogon) do Windows está desativado. Ative-o em services.msc e tente de novo.",
        "The Windows \"Secondary Logon\" service (seclogon) is disabled. Enable it in services.msc and try again.");
    public static string ErrLogonType => T(
        "Uma política do Windows impede este usuário de iniciar programas (logon interativo negado).",
        "A Windows policy prevents this user from starting programs (interactive logon denied).");
    public static string ErrAccountDisabled => T(
        "O usuário do Windows deste perfil está desativado ou bloqueado.",
        "This profile's Windows user is disabled or locked out.");
    public static string ErrRunAs => T("O Windows não conseguiu iniciar o processo no perfil: {0} ({1})", "Windows couldn't start the process in the profile: {0} ({1})");
    public static string ErrStep => T("Uma etapa falhou dentro do perfil:\n{0}", "A step failed inside the profile:\n{0}");
    public static string ErrUserExists => T("Já existe um usuário do Windows chamado \"{0}\".", "A Windows user named \"{0}\" already exists.");
    public static string ErrUserMissing => T("O usuário do Windows \"{0}\" não existe mais. Remova o perfil e crie de novo.", "The Windows user \"{0}\" no longer exists. Remove the profile and create it again.");
    public static string ErrPasswordPolicy => T("A política de senhas deste computador recusou a senha gerada.", "This computer's password policy rejected the generated password.");
    public static string ErrCreateUser => T("Não foi possível criar o usuário do Windows: {0}", "Couldn't create the Windows user: {0}");
    public static string ErrGroup => T("Não foi possível alterar o grupo \"{0}\" do usuário: {1}", "Couldn't change the user's \"{0}\" group membership: {1}");
    public static string ErrDeleteUser => T("Não foi possível excluir o usuário do Windows: {0}", "Couldn't delete the Windows user: {0}");
    public static string ErrFolderMissing => T("A pasta não existe: {0}", "Folder not found: {0}");
    public static string ErrAcl => T("Não foi possível dar acesso à pasta {0}:\n{1}", "Couldn't grant access to {0}:\n{1}");
    public static string ErrElevatedOtherUser => T(
        "A permissão de administrador foi dada com outra conta. Use o app numa conta do Windows que seja administradora.",
        "Administrator permission was granted with a different account. Run the app from a Windows account that is an administrator.");
    public static string ErrElevatedNoResult => T("A etapa de administrador não retornou resultado.", "The administrator step returned no result.");
    public static string ErrClaudeMissingInProfile => T(
        "O Claude não está instalado neste perfil. Use ⋯ → Reparar.",
        "Claude isn't installed in this profile. Use ⋯ → Repair.");
    public static string ErrNeedInstaller => T("É preciso escolher o instalador do Claude.", "The Claude installer is required.");
    public static string ErrWorkFolderMissing => T(
        "A pasta de trabalho deste perfil não existe. Use ⋯ → Reparar.",
        "This profile's work folder is missing. Use ⋯ → Repair.");
    public static string ErrSecureFolder => T(
        "Não foi possível proteger a pasta {0}: {1}\nApague essa pasta manualmente e tente de novo.",
        "Couldn't secure the folder {0}: {1}\nDelete that folder manually and try again.");
    public static string ErrMsixRegister => T(
        "Não foi possível instalar o Claude neste perfil, nem depois de liberar o pacote com permissão de administrador.\n\n{0}",
        "Couldn't install Claude in this profile, even after provisioning the package with administrator permission.\n\n{0}");
    public static string ErrProvision => T(
        "Não foi possível liberar o pacote do Claude para outros usuários do Windows:\n{0}",
        "Couldn't provision the Claude package for other Windows users:\n{0}");
    public static string WarnProfileLeftover => T(
        "O perfil foi removido, mas o Windows não conseguiu apagar a pasta {0}, que ainda guarda os dados dele (incluindo o login do Claude). Reinicie o computador e apague essa pasta manualmente.",
        "The profile was removed, but Windows couldn't delete {0}, which still holds its data (including the Claude sign-in). Restart the computer and delete that folder manually.");
    public static string NoLogsYet => T("Ainda não há logs.", "There are no logs yet.");
    public static string ErrPasswordUnreadable => T(
        "Não foi possível ler a senha guardada deste perfil. Ela só abre na conta do Windows que criou o perfil. Remova o perfil e crie de novo.",
        "Couldn't read this profile's stored password. It only opens in the Windows account that created the profile. Remove the profile and create it again.");
    public static string ErrInvalidProfileUser => T(
        "\"{0}\" não é um usuário criado por este app; a operação foi recusada.",
        "\"{0}\" isn't a user created by this app; the operation was refused.");

    public static string UserComment => "Claude Multi-Account profile";
    public static string TerminalHint => T(
        "Dica: para usar o Claude Code (CLI) neste perfil, instale com: irm https://claude.ai/install.ps1 | iex",
        "Tip: to use Claude Code (CLI) in this profile, install it with: irm https://claude.ai/install.ps1 | iex");
}
