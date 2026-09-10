use std::env;
use std::path::{Path, PathBuf};

/// Render a path for persisted CLI metadata without exposing this checkout's
/// machine-specific home or repository prefix.
pub(crate) fn display_path(path: &Path, repository_root: &Path) -> String {
    display_path_with_home(path, repository_root, home_dir().as_deref())
}

fn display_path_with_home(path: &Path, repository_root: &Path, home: Option<&Path>) -> String {
    let absolute = absolute_path(path);
    let repository_root = checkout_root(repository_root);

    if let Ok(relative) = absolute.strip_prefix(&repository_root) {
        return slash_path(relative);
    }

    if let Some(home) = home {
        let home = absolute_path(home);
        if let Ok(relative) = absolute.strip_prefix(&home) {
            let relative = slash_path(relative);
            return if relative == "." {
                "$HOME".to_string()
            } else {
                format!("$HOME/{relative}")
            };
        }
    }

    slash_path(&absolute)
}

fn checkout_root(base_dir: &Path) -> PathBuf {
    let absolute = absolute_path(base_dir);
    absolute
        .ancestors()
        .find(|candidate| candidate.join(".git").exists())
        .map(Path::to_path_buf)
        .unwrap_or(absolute)
}

fn home_dir() -> Option<PathBuf> {
    env::var_os("HOME")
        .or_else(|| env::var_os("USERPROFILE"))
        .map(PathBuf::from)
}

fn absolute_path(path: &Path) -> PathBuf {
    let absolute = if path.is_absolute() {
        path.to_path_buf()
    } else {
        env::current_dir()
            .map(|cwd| cwd.join(path))
            .unwrap_or_else(|_| path.to_path_buf())
    };
    absolute.canonicalize().unwrap_or(absolute)
}

fn slash_path(path: &Path) -> String {
    let rendered = path.to_string_lossy().replace('\\', "/");
    if rendered.is_empty() {
        ".".to_string()
    } else {
        rendered
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn repository_paths_are_relative_to_repository_root() {
        assert_eq!(
            display_path_with_home(
                Path::new("/workspace/alice/repo/spirectl/tests/snapshots/snapshot.json"),
                Path::new("/workspace/alice/repo/spirectl"),
                Some(Path::new("/workspace/alice")),
            ),
            "tests/snapshots/snapshot.json"
        );
    }

    #[test]
    fn home_paths_use_home_placeholder() {
        assert_eq!(
            display_path_with_home(
                Path::new("/workspace/alice/.config/spirectl/config.yaml"),
                Path::new("/work/spirectl"),
                Some(Path::new("/workspace/alice")),
            ),
            "$HOME/.config/spirectl/config.yaml"
        );
        assert_eq!(
            display_path_with_home(
                Path::new("/workspace/alice"),
                Path::new("/work/spirectl"),
                Some(Path::new("/workspace/alice")),
            ),
            "$HOME"
        );
    }

    #[test]
    fn unrelated_paths_remain_absolute() {
        assert_eq!(
            display_path_with_home(
                Path::new("/tmp/visual-baseline.png"),
                Path::new("/work/spirectl"),
                Some(Path::new("/workspace/alice")),
            ),
            "/tmp/visual-baseline.png"
        );
    }

    #[test]
    fn path_separators_are_stable() {
        assert_eq!(
            slash_path(Path::new(r"tests\snapshots\snapshot.json")),
            "tests/snapshots/snapshot.json"
        );
    }
}
