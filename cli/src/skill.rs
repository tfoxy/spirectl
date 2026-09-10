use crate::{AppContext, AppError};
use clap::{ArgGroup, Args, Subcommand};
use serde::Serialize;
use serde_json::{Value, json};
use std::fs;
use std::path::PathBuf;

const SKILL_NAME: &str = "spirectl";
const SOURCE_PATH: &str = "skills/spirectl/SKILL.md";
const SKILL_PACK: &[SkillPackFile] = &[
    SkillPackFile {
        source_path: "skills/spirectl/SKILL.md",
        relative_path: "SKILL.md",
        kind: SkillPackFileKind::Skill,
        contents: include_str!("../../skills/spirectl/SKILL.md"),
    },
    SkillPackFile {
        source_path: "skills/spirectl/references/cli-runtime.md",
        relative_path: "references/cli-runtime.md",
        kind: SkillPackFileKind::Reference,
        contents: include_str!("../../skills/spirectl/references/cli-runtime.md"),
    },
    SkillPackFile {
        source_path: "skills/spirectl/references/library-integration.md",
        relative_path: "references/library-integration.md",
        kind: SkillPackFileKind::Reference,
        contents: include_str!("../../skills/spirectl/references/library-integration.md"),
    },
    SkillPackFile {
        source_path: "skills/spirectl/references/render-assets.md",
        relative_path: "references/render-assets.md",
        kind: SkillPackFileKind::Reference,
        contents: include_str!("../../skills/spirectl/references/render-assets.md"),
    },
    SkillPackFile {
        source_path: "skills/spirectl/references/testing-debugging.md",
        relative_path: "references/testing-debugging.md",
        kind: SkillPackFileKind::Reference,
        contents: include_str!("../../skills/spirectl/references/testing-debugging.md"),
    },
    SkillPackFile {
        source_path: "skills/spirectl/references/static-modding.md",
        relative_path: "references/static-modding.md",
        kind: SkillPackFileKind::Reference,
        contents: include_str!("../../skills/spirectl/references/static-modding.md"),
    },
];

#[derive(Debug, Clone, Copy)]
struct SkillPackFile {
    source_path: &'static str,
    relative_path: &'static str,
    kind: SkillPackFileKind,
    contents: &'static str,
}

#[derive(Debug, Clone, Copy, Serialize)]
#[serde(rename_all = "kebab-case")]
enum SkillPackFileKind {
    Skill,
    Reference,
}

#[derive(Debug, Clone, Args)]
#[command(about = "Install checked-in spirectl skill files into a project-local or explicit root.")]
pub struct SkillCommand {
    #[command(subcommand)]
    pub command: SkillSubcommand,
}

#[derive(Debug, Clone, Subcommand)]
pub enum SkillSubcommand {
    #[command(about = "Install the checked-in spirectl skill into a managed or explicit root.")]
    Install(SkillInstallArgs),
}

#[derive(Debug, Clone, Args, Default)]
#[command(
    about = "Write the checked-in spirectl skill into ./.agents/skills or an explicit root path."
)]
#[command(group(
    ArgGroup::new("path_input")
        .required(false)
        .multiple(false)
        .args(["path", "path_flag"])
))]
pub struct SkillInstallArgs {
    #[arg(value_name = "PATH")]
    pub path: Option<PathBuf>,

    #[arg(
        long = "path",
        value_name = "PATH",
        help = "Override the skill root directory instead of using the project-local .agents/skills default"
    )]
    pub path_flag: Option<PathBuf>,
}

impl SkillInstallArgs {
    pub fn explicit_path(&self) -> Option<&PathBuf> {
        self.path.as_ref().or(self.path_flag.as_ref())
    }
}

#[derive(Debug, Clone, Copy, Serialize)]
#[serde(rename_all = "kebab-case")]
enum PathSource {
    ManagedDefault,
    Explicit,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct InstalledSkill {
    source: &'static str,
    source_path: &'static str,
    name: &'static str,
    path: String,
    skill_dir: String,
    path_source: PathSource,
    managed_dir: Option<String>,
    installed_files: Vec<InstalledSkillFile>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct InstalledSkillFile {
    source_path: &'static str,
    relative_path: &'static str,
    path: String,
    kind: SkillPackFileKind,
}

pub(crate) fn execute_skill_install_json(
    args: SkillInstallArgs,
    context: AppContext<'_>,
) -> Result<Value, AppError> {
    let (root_dir, path_source) = match args.explicit_path() {
        Some(path) => (path.clone(), PathSource::Explicit),
        None => (
            context
                .config_origin
                .base_dir
                .join(".agents")
                .join("skills"),
            PathSource::ManagedDefault,
        ),
    };
    let primary_skill_dir = root_dir.join(SKILL_NAME);
    let primary_install_path = primary_skill_dir.join("SKILL.md");
    let mut installed_files = Vec::new();

    for file in SKILL_PACK {
        let install_path = primary_skill_dir.join(file.relative_path);
        let install_dir = install_path.parent().unwrap_or(&primary_skill_dir);
        fs::create_dir_all(install_dir).map_err(|source| AppError {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "skill_install_failed",
                    "message": "failed to create skill install directory",
                    "path": install_dir.display().to_string(),
                    "details": source.to_string(),
                }
            }),
        })?;

        fs::write(&install_path, file.contents).map_err(|source| AppError {
            exit_code: 2,
            payload: json!({
                "error": {
                    "code": "skill_install_failed",
                    "message": "failed to write skill file",
                    "path": install_path.display().to_string(),
                    "details": source.to_string(),
                }
            }),
        })?;

        installed_files.push(InstalledSkillFile {
            source_path: file.source_path,
            relative_path: file.relative_path,
            path: install_path.display().to_string(),
            kind: file.kind,
        });
    }

    serde_json::to_value(InstalledSkill {
        source: "checked-in-skill-pack",
        source_path: SOURCE_PATH,
        name: SKILL_NAME,
        path: primary_install_path.display().to_string(),
        skill_dir: primary_skill_dir.display().to_string(),
        path_source,
        managed_dir: match path_source {
            PathSource::ManagedDefault => Some(root_dir.display().to_string()),
            PathSource::Explicit => None,
        },
        installed_files,
    })
    .map_err(|source| AppError {
        exit_code: 5,
        payload: json!({
            "error": {
                "code": "serialization_failed",
                "message": source.to_string(),
            }
        }),
    })
}
