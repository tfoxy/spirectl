use crate::{AppContext, AppError, Cli, RenderedCommand};
use clap::{Args, Command, CommandFactory, Subcommand, ValueEnum};
use clap_complete::{Generator, Shell};
use serde::{Deserialize, Serialize};
use serde_json::json;
use std::ffi::OsStr;
use std::fs;
use std::path::{Path, PathBuf};

#[derive(Debug, Clone, Copy, Serialize, Deserialize, ValueEnum, PartialEq, Eq)]
#[serde(rename_all = "kebab-case")]
pub enum CompletionShell {
    Bash,
    Zsh,
    Fish,
    Powershell,
}

impl CompletionShell {
    fn generator(self) -> impl Generator {
        match self {
            Self::Bash => Shell::Bash,
            Self::Zsh => Shell::Zsh,
            Self::Fish => Shell::Fish,
            Self::Powershell => Shell::PowerShell,
        }
    }

    fn file_name(self) -> &'static str {
        match self {
            Self::Bash => "sts2.bash",
            Self::Zsh => "sts2.zsh",
            Self::Fish => "sts2.fish",
            Self::Powershell => "sts2.ps1",
        }
    }

    fn activation_command(self, path: &Path) -> String {
        match self {
            Self::Powershell => format!(". {}", powershell_quote(path)),
            Self::Bash | Self::Zsh | Self::Fish => format!("source {}", posix_shell_quote(path)),
        }
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct ActivationStrategy {
    id: &'static str,
    summary: &'static str,
    command: String,
    loads_on_demand: bool,
    notes: Vec<String>,
}

#[derive(Debug, Clone)]
enum PosixSuggestedPath {
    Exact(PathBuf),
    ShellHomeRelative(&'static str),
}

impl PosixSuggestedPath {
    fn render(&self) -> String {
        match self {
            Self::Exact(path) => posix_shell_quote(path),
            Self::ShellHomeRelative(path) => (*path).to_string(),
        }
    }

    fn join(&self, child: &'static str) -> Self {
        match self {
            Self::Exact(path) => Self::Exact(path.join(child)),
            Self::ShellHomeRelative(path) => Self::ShellHomeRelative(match (*path, child) {
                ("~/.local/share/bash-completion/completions", "sts2.bash") => {
                    "~/.local/share/bash-completion/completions/sts2.bash"
                }
                ("~/.config/fish/completions", "sts2.fish") => {
                    "~/.config/fish/completions/sts2.fish"
                }
                _ => unreachable!("unsupported shell-relative completion path"),
            }),
        }
    }
}

#[derive(Debug, Clone, Args, Default)]
#[command(about = "Generate shell completion text for the current shell or an explicit shell.")]
pub struct CompletionCommand {
    #[command(subcommand)]
    pub command: Option<CompletionSubcommand>,
}

#[derive(Debug, Clone, Subcommand)]
pub enum CompletionSubcommand {
    #[command(about = "Generate Bash completion text.")]
    Bash,
    #[command(about = "Generate Zsh completion text.")]
    Zsh,
    #[command(about = "Generate Fish completion text.")]
    Fish,
    #[command(about = "Generate PowerShell completion text.")]
    Powershell,
    #[command(about = "Install shell completion text and print activation metadata.")]
    Install(CompletionInstallArgs),
}

#[derive(Debug, Clone, Args, Default)]
#[command(about = "Write shell completion text to a file for the detected or specified shell.")]
pub struct CompletionInstallArgs {
    #[arg(value_enum, help = "Shell to generate or install completion for")]
    pub shell: Option<CompletionShell>,

    #[arg(
        long,
        help = "Override the output path instead of using the managed per-user default"
    )]
    pub path: Option<PathBuf>,
}

#[derive(Debug, Clone, Copy, Serialize)]
#[serde(rename_all = "kebab-case")]
enum ShellSource {
    Explicit,
    Detected,
}

#[derive(Debug, Clone, Copy, Serialize)]
#[serde(rename_all = "kebab-case")]
enum PathSource {
    ManagedDefault,
    Explicit,
}

#[derive(Debug, Clone, Copy)]
struct ShellResolution {
    shell: CompletionShell,
    source: ShellSource,
}

pub(crate) fn handle_completion(
    command: CompletionCommand,
    context: AppContext<'_>,
) -> Result<RenderedCommand, AppError> {
    match command.command {
        None => render_generate(None, context),
        Some(CompletionSubcommand::Bash) => render_generate(Some(CompletionShell::Bash), context),
        Some(CompletionSubcommand::Zsh) => render_generate(Some(CompletionShell::Zsh), context),
        Some(CompletionSubcommand::Fish) => render_generate(Some(CompletionShell::Fish), context),
        Some(CompletionSubcommand::Powershell) => {
            render_generate(Some(CompletionShell::Powershell), context)
        }
        Some(CompletionSubcommand::Install(args)) => install_completion(args, context),
    }
}

fn render_generate(
    explicit_shell: Option<CompletionShell>,
    context: AppContext<'_>,
) -> Result<RenderedCommand, AppError> {
    let resolution = resolve_shell(explicit_shell)?;
    let script = generate_script(resolution.shell);

    if context.json_output {
        return Ok(RenderedCommand {
            stdout: serde_json::to_string_pretty(&json!({
                "source": "clap-complete",
                "command": "sts2",
                "shell": resolution.shell,
                "shellSource": resolution.source,
                "script": script,
            }))
            .expect("completion json")
                + "\n",
            exit_code: 0,
        });
    }

    Ok(RenderedCommand {
        stdout: script,
        exit_code: 0,
    })
}

fn install_completion(
    args: CompletionInstallArgs,
    context: AppContext<'_>,
) -> Result<RenderedCommand, AppError> {
    let resolution = resolve_shell(args.shell)?;
    let path_source = if args.path.is_some() {
        PathSource::Explicit
    } else {
        PathSource::ManagedDefault
    };
    let install_path = match args.path {
        Some(path) => path,
        None => default_install_path(resolution.shell)?,
    };
    let script = generate_script(resolution.shell);

    if let Some(parent) = install_path.parent() {
        fs::create_dir_all(parent).map_err(|source| AppError {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "completion_install_failed",
                    "message": "failed to create completion install directory",
                    "path": parent.display().to_string(),
                    "details": source.to_string()
                }
            }),
        })?;
    }

    fs::write(&install_path, &script).map_err(|source| AppError {
        exit_code: 2,
        payload: json!({
            "error": {
                "code": "completion_install_failed",
                "message": "failed to write completion script",
                "path": install_path.display().to_string(),
                "details": source.to_string()
            }
        }),
    })?;

    let activation_strategies = activation_strategies(resolution.shell, &install_path);
    let activation_command = activation_strategies
        .first()
        .map(|strategy| strategy.command.clone())
        .expect("completion activation strategy");

    let payload = json!({
        "source": "clap-complete",
        "command": "sts2",
        "shell": resolution.shell,
        "shellSource": resolution.source,
        "path": install_path.display().to_string(),
        "pathSource": path_source,
        "activationCommand": activation_command,
        "activationStrategies": activation_strategies,
        "managedDir": match path_source {
            PathSource::ManagedDefault => install_path.parent().map(|path| path.display().to_string()),
            PathSource::Explicit => None::<String>,
        },
    });

    Ok(RenderedCommand {
        stdout: if context.json_output {
            serde_json::to_string_pretty(&payload).expect("install json") + "\n"
        } else {
            serde_yaml::to_string(&payload).expect("install yaml")
        },
        exit_code: 0,
    })
}

fn resolve_shell(explicit_shell: Option<CompletionShell>) -> Result<ShellResolution, AppError> {
    if let Some(shell) = explicit_shell {
        return Ok(ShellResolution {
            shell,
            source: ShellSource::Explicit,
        });
    }

    detect_shell().map(|shell| ShellResolution {
        shell,
        source: ShellSource::Detected,
    })
}

fn detect_shell() -> Result<CompletionShell, AppError> {
    let shell = std::env::var_os("SHELL")
        .filter(|value| !value.is_empty())
        .map(PathBuf::from)
        .and_then(|path| path.file_name().map(OsStr::to_os_string));

    let Some(shell) = shell else {
        return detect_powershell_from_env().ok_or_else(|| {
            shell_detection_error("could not detect the current shell; pass one explicitly")
        });
    };

    let normalized = shell
        .to_string_lossy()
        .trim_start_matches('-')
        .to_ascii_lowercase();
    let normalized = normalized.strip_suffix(".exe").unwrap_or(&normalized);

    let detected = match normalized {
        "bash" => Some(CompletionShell::Bash),
        "zsh" => Some(CompletionShell::Zsh),
        "fish" => Some(CompletionShell::Fish),
        "pwsh" | "powershell" => Some(CompletionShell::Powershell),
        _ => None,
    };

    detected.ok_or_else(|| {
        shell_detection_error(&format!(
            "unsupported shell '{normalized}'; pass one of bash, zsh, fish, or powershell explicitly"
        ))
    })
}

fn detect_powershell_from_env() -> Option<CompletionShell> {
    std::env::var_os("PSModulePath")
        .filter(|value| !value.is_empty())
        .map(|_| CompletionShell::Powershell)
}

fn shell_detection_error(message: &str) -> AppError {
    AppError {
        exit_code: 2,
        payload: json!({
            "error": {
                "code": "shell_detection_failed",
                "message": message
            }
        }),
    }
}

fn default_install_path(shell: CompletionShell) -> Result<PathBuf, AppError> {
    let base_dir = completion_home_dir().ok_or_else(|| AppError {
        exit_code: 2,
        payload: json!({
            "error": {
                "code": "completion_install_failed",
                "message": "could not resolve a per-user completion directory; pass --path explicitly"
            }
        }),
    })?;

    Ok(base_dir
        .join("spirectl")
        .join("completions")
        .join(shell.file_name()))
}

fn activation_strategies(shell: CompletionShell, install_path: &Path) -> Vec<ActivationStrategy> {
    let mut strategies = vec![ActivationStrategy {
        id: "source",
        summary: "Load the installed completion file in the current shell.",
        command: shell.activation_command(install_path),
        loads_on_demand: false,
        notes: Vec::new(),
    }];

    match shell {
        CompletionShell::Bash => {
            let completion_dir = bash_completion_user_dir();
            let completion_path = completion_dir.join("sts2.bash");
            strategies.push(ActivationStrategy {
                id: "bash-completion-user-dir",
                summary:
                    "Symlink into bash-completion's per-user completions directory for on-demand loading.",
                command: format!(
                    "mkdir -p {} && ln -sf {} {}",
                    completion_dir.render(),
                    posix_shell_quote(install_path),
                    completion_path.render()
                ),
                loads_on_demand: true,
                notes: vec![
                    "This requires bash-completion to be installed and loaded by your Bash startup."
                        .to_string(),
                ],
            });
        }
        CompletionShell::Fish => {
            let completion_dir = fish_completion_user_dir();
            let completion_path = completion_dir.join("sts2.fish");
            strategies.push(ActivationStrategy {
                id: "fish-user-completions",
                summary: "Symlink into Fish's user completions directory for on-demand loading.",
                command: format!(
                    "mkdir -p {} && ln -sf {} {}",
                    completion_dir.render(),
                    posix_shell_quote(install_path),
                    completion_path.render()
                ),
                loads_on_demand: true,
                notes: vec![
                    "Fish automatically discovers completion files from this directory when needed."
                        .to_string(),
                ],
            });
        }
        CompletionShell::Zsh => {
            if let Some(completion_dir) = detect_zsh_user_fpath_dir() {
                strategies.push(ActivationStrategy {
                    id: "zsh-fpath",
                    summary:
                        "Symlink into a user-owned directory already present in $fpath for autoloaded completion.",
                    command: format!(
                        "mkdir -p {} && ln -sf {} {}",
                        posix_shell_quote(&completion_dir),
                        posix_shell_quote(install_path),
                        posix_shell_quote(&completion_dir.join("_sts2"))
                    ),
                    loads_on_demand: true,
                    notes: vec![
                        "The target directory must already be present in $fpath.".to_string(),
                        "Your shell startup must run compinit for autoloaded completions to activate.".to_string(),
                    ],
                });
            }
        }
        CompletionShell::Powershell => {}
    }

    strategies
}

#[cfg(windows)]
fn completion_home_dir() -> Option<PathBuf> {
    env_path("APPDATA")
        .or_else(|| env_path("USERPROFILE").map(|path| path.join("AppData").join("Roaming")))
}

#[cfg(not(windows))]
fn completion_home_dir() -> Option<PathBuf> {
    env_path("XDG_CONFIG_HOME").or_else(|| env_path("HOME").map(|path| path.join(".config")))
}

fn generate_script(shell: CompletionShell) -> String {
    let mut command = completion_command();
    let mut output = Vec::new();
    clap_complete::generate(shell.generator(), &mut command, "sts2", &mut output);
    String::from_utf8(output)
        .expect("completion should be utf8")
        .replace("__fixture_load_compat", "fixture")
        .replace("fixture fixture", "fixture")
}

fn completion_command() -> Command {
    Cli::command().mut_subcommands(|subcommand| {
        if subcommand.get_name() != "dev" {
            return subcommand;
        }

        subcommand.mut_subcommands(|dev_subcommand| {
            if dev_subcommand.get_name() == "load-fixture" {
                dev_subcommand.name("__fixture_load_compat").hide(true)
            } else {
                dev_subcommand
            }
        })
    })
}

fn bash_completion_user_dir() -> PosixSuggestedPath {
    std::env::var_os("XDG_DATA_HOME")
        .filter(|value| !value.is_empty())
        .map(PathBuf::from)
        .filter(|path| path.is_absolute())
        .map(|path| PosixSuggestedPath::Exact(path.join("bash-completion").join("completions")))
        .or_else(|| {
            env_path("HOME").map(|path| {
                PosixSuggestedPath::Exact(
                    path.join(".local")
                        .join("share")
                        .join("bash-completion")
                        .join("completions"),
                )
            })
        })
        .unwrap_or(PosixSuggestedPath::ShellHomeRelative(
            "~/.local/share/bash-completion/completions",
        ))
}

fn fish_completion_user_dir() -> PosixSuggestedPath {
    env_path("XDG_CONFIG_HOME")
        .map(|path| PosixSuggestedPath::Exact(path.join("fish").join("completions")))
        .or_else(|| {
            env_path("HOME").map(|path| {
                PosixSuggestedPath::Exact(path.join(".config").join("fish").join("completions"))
            })
        })
        .unwrap_or(PosixSuggestedPath::ShellHomeRelative(
            "~/.config/fish/completions",
        ))
}

fn detect_zsh_user_fpath_dir() -> Option<PathBuf> {
    let home = env_path("HOME")?;
    let fpath = std::env::var_os("FPATH")?;

    std::env::split_paths(&fpath).find(|path| path.is_absolute() && path.starts_with(&home))
}

fn posix_shell_quote(path: &Path) -> String {
    if path
        .to_string_lossy()
        .chars()
        .all(|char| char.is_ascii_alphanumeric() || matches!(char, '/' | '.' | '_' | '-'))
    {
        path.display().to_string()
    } else {
        format!("'{}'", path.display().to_string().replace('\'', "'\\''"))
    }
}

fn powershell_quote(path: &Path) -> String {
    format!("'{}'", path.display().to_string().replace('\'', "''"))
}

fn env_path(name: &str) -> Option<PathBuf> {
    std::env::var_os(name)
        .filter(|value| !value.is_empty())
        .map(PathBuf::from)
        .filter(|path| path.is_absolute())
}
