//! Fork (AryanSenthil/Konnect): keep KiCad's Schematic Editor open while Konnect edits the schematic.
//!
//! Upstream Konnect refuses every schematic write while KiCad's `~<file>.kicad_sch.lck` exists,
//! because KiCad 10's Schematic Editor keeps its own copy in memory and its next save silently
//! overwrites an outside edit. KiCad 10's IPC API cannot save or revert a schematic (that arrives
//! in KiCad 11), so this module uses `kicad-editor-bridge.exe`, which drives the editor's own
//! File > Save and File > Revert menu items:
//!
//! 1. First write to a schematic the editor has open: the bridge saves the user's unsaved work
//!    (File > Save) and confirms no dialog is open. The write proceeds.
//! 2. Further writes in the same burst proceed as long as the editor stays clean.
//! 3. `KONNECT_LIVE_EDITOR_DEBOUNCE_MS` (default 1500) after the last write, the bridge reverts
//!    the editor to the saved file (File > Revert, answering KiCad's confirmation), so the user
//!    sees the edit without closing anything.
//! 4. If the user edits in KiCad between a write and the reload, nothing is reverted: the next
//!    write is refused with instructions, because only the user can choose whose edit wins.
//!
//! `KONNECT_LIVE_EDITOR=0` restores upstream behaviour. `KONNECT_EDITOR_BRIDGE` overrides the
//! bridge path (default: `kicad-editor-bridge.exe` beside `konnect.exe`). Windows only.

/// Install the live-editor hook if enabled and the bridge is present. No-op off Windows.
pub fn install() {
    #[cfg(windows)]
    imp::install();
}

#[cfg(windows)]
mod imp {
    use serde_json::Value;
    use std::collections::HashMap;
    use std::os::windows::process::CommandExt;
    use std::path::{Path, PathBuf};
    use std::process::Command;
    use std::sync::{Arc, Mutex};
    use std::time::{Duration, Instant};
    use tracing::{info, warn};

    const CREATE_NO_WINDOW: u32 = 0x0800_0000;
    const DEFAULT_DEBOUNCE_MS: u64 = 1500;
    const CONFLICT: &str = "you changed the schematic in KiCad after Claude's last edit, before KiCad reloaded it. \
To keep Claude's edit, use File > Revert in KiCad. To keep yours, press Ctrl+S in KiCad \
(Claude's edit is then overwritten and must be redone). Then retry";

    struct Session {
        last_write: Instant,
        conflict: Option<String>,
    }

    type Sessions = Arc<Mutex<HashMap<PathBuf, Session>>>;

    struct Reply {
        ok: bool,
        json: Value,
    }

    impl Reply {
        fn flag(&self, name: &str) -> bool {
            self.json.get(name).and_then(Value::as_bool).unwrap_or(false)
        }
        fn code(&self) -> String {
            self.json
                .get("error")
                .and_then(Value::as_str)
                .unwrap_or("unknown")
                .to_owned()
        }
    }

    pub fn install() {
        if matches!(
            std::env::var("KONNECT_LIVE_EDITOR").ok().as_deref(),
            Some("0") | Some("off") | Some("false")
        ) {
            info!("live editor: disabled by KONNECT_LIVE_EDITOR");
            return;
        }
        let Some(bridge) = bridge_path() else {
            warn!(
                "live editor: kicad-editor-bridge.exe not found beside konnect.exe or at \
                 KONNECT_EDITOR_BRIDGE; schematic writes refuse while KiCad has the file open"
            );
            return;
        };
        let debounce = Duration::from_millis(
            std::env::var("KONNECT_LIVE_EDITOR_DEBOUNCE_MS")
                .ok()
                .and_then(|value| value.parse().ok())
                .unwrap_or(DEFAULT_DEBOUNCE_MS),
        );
        let sessions: Sessions = Arc::new(Mutex::new(HashMap::new()));
        {
            let sessions = sessions.clone();
            let bridge = bridge.clone();
            if let Err(error) = std::thread::Builder::new()
                .name("konnect-live-editor".into())
                .spawn(move || reload_loop(sessions, bridge, debounce))
            {
                warn!("live editor: could not start the reload thread ({error}); staying upstream");
                return;
            }
        }
        let hook_bridge = bridge.clone();
        if konnect_sexp::writer::set_editor_lock_hook(Box::new(move |path, _lock| {
            prepare(&sessions, &hook_bridge, path)
        })) {
            info!(
                "live editor: enabled via {} (editor reloads {} ms after the last write)",
                bridge.display(),
                debounce.as_millis()
            );
        }
    }

    fn bridge_path() -> Option<PathBuf> {
        if let Some(path) = std::env::var_os("KONNECT_EDITOR_BRIDGE").map(PathBuf::from) {
            return path.is_file().then_some(path);
        }
        let candidate = std::env::current_exe()
            .ok()?
            .parent()?
            .join("kicad-editor-bridge.exe");
        candidate.is_file().then_some(candidate)
    }

    fn key(path: &Path) -> PathBuf {
        path.canonicalize().unwrap_or_else(|_| path.to_path_buf())
    }

    fn run(bridge: &Path, command: &str, path: &Path) -> Result<Reply, String> {
        let output = Command::new(bridge)
            .arg(command)
            .arg(path)
            .creation_flags(CREATE_NO_WINDOW)
            .output()
            .map_err(|error| format!("could not run the editor bridge ({error})"))?;
        let stdout = String::from_utf8_lossy(&output.stdout);
        let line = stdout
            .lines()
            .rev()
            .find(|line| line.trim_start().starts_with('{'))
            .unwrap_or("");
        let json: Value = serde_json::from_str(line).map_err(|_| {
            format!(
                "the editor bridge gave no answer (exit {:?}): {}",
                output.status.code(),
                stdout.trim()
            )
        })?;
        let ok = json.get("ok").and_then(Value::as_bool).unwrap_or(false);
        Ok(Reply { ok, json })
    }

    fn explain(reply: &Reply) -> String {
        match reply.code().as_str() {
            "no_editor" => "KiCad has a lock on this schematic, but no open Schematic Editor window shows it \
(the lock may be stale or held on another computer). Close KiCad's editor for this file, or delete \
the stale ~*.lck only after confirming no KiCad owns it, then retry"
                .to_owned(),
            "ambiguous_editor" => {
                "two Schematic Editor windows show a schematic with this name; close one, then retry"
                    .to_owned()
            }
            "dialog_open" => "a dialog is open in KiCad's Schematic Editor (for example Plot or Properties). \
Close it, then retry; Konnect saves and reloads the editor itself"
                .to_owned(),
            "unexpected_dialog" => format!(
                "KiCad opened a dialog while saving ({}); answer it in KiCad, then retry",
                reply
                    .json
                    .get("dialog_title")
                    .and_then(Value::as_str)
                    .unwrap_or("untitled")
            ),
            "save_timeout" => "KiCad did not finish saving within 20 seconds; retry".to_owned(),
            other => format!("editor bridge error: {other}"),
        }
    }

    /// The hook body: decide whether a write may proceed under KiCad's lock.
    fn prepare(sessions: &Sessions, bridge: &Path, path: &Path) -> Result<(), String> {
        let mut map = sessions
            .lock()
            .map_err(|_| "live editor state is poisoned; restart Konnect".to_owned())?;
        let k = key(path);

        if let Some(session) = map.get_mut(&k) {
            // A burst is in progress: the editor was saved before the first write and has
            // not reloaded yet. It must still be clean, or the user has edited stale content.
            let status = run(bridge, "status", path)?;
            if !status.flag("editor") {
                map.remove(&k);
                return Err(explain(&Reply {
                    ok: false,
                    json: serde_json::json!({ "error": "no_editor" }),
                }));
            }
            if status.flag("dialog_open") {
                return Err(explain(&Reply {
                    ok: false,
                    json: serde_json::json!({ "error": "dialog_open" }),
                }));
            }
            if status.flag("dirty") {
                session.conflict = Some(CONFLICT.to_owned());
                return Err(CONFLICT.to_owned());
            }
            // Clean again after a conflict means the user chose (Revert or Ctrl+S);
            // either way the file on disk is now the truth.
            session.conflict = None;
            session.last_write = Instant::now();
            return Ok(());
        }

        let reply = run(bridge, "save", path)?;
        if !reply.ok {
            return Err(explain(&reply));
        }
        // Resolve outside the macro: inside `info!`, `Value` names tracing's trait.
        let action = reply
            .json
            .get("action")
            .and_then(serde_json::Value::as_str)
            .unwrap_or("?")
            .to_owned();
        info!(
            path = %path.display(),
            action = %action,
            "live editor: KiCad's editor is safe to write under"
        );
        map.insert(
            k,
            Session {
                last_write: Instant::now(),
                conflict: None,
            },
        );
        Ok(())
    }

    fn reload_loop(sessions: Sessions, bridge: PathBuf, debounce: Duration) {
        loop {
            std::thread::sleep(Duration::from_millis(200));
            let Ok(mut map) = sessions.lock() else {
                return;
            };
            let due: Vec<PathBuf> = map
                .iter()
                .filter(|(_, session)| {
                    session.conflict.is_none() && session.last_write.elapsed() >= debounce
                })
                .map(|(path, _)| path.clone())
                .collect();
            for path in due {
                let outcome = run(&bridge, "reload", &path);
                match outcome {
                    Ok(reply) if reply.ok => {
                        info!(path = %path.display(), "live editor: reloaded KiCad's Schematic Editor");
                        map.remove(&path);
                    }
                    Ok(reply) => {
                        let code = reply.code();
                        match code.as_str() {
                            // Editor closed meanwhile: KiCad will open the file from disk.
                            "no_editor" => {
                                map.remove(&path);
                            }
                            "unsaved_changes_in_editor" => {
                                warn!(path = %path.display(), "live editor: user edited before reload; waiting for them to choose");
                                if let Some(session) = map.get_mut(&path) {
                                    session.conflict = Some(CONFLICT.to_owned());
                                }
                            }
                            // A dialog is open, or a transient failure: try again later.
                            _ => {
                                warn!(path = %path.display(), code = %code, "live editor: reload deferred");
                                if let Some(session) = map.get_mut(&path) {
                                    session.last_write = Instant::now();
                                }
                            }
                        }
                    }
                    Err(error) => {
                        warn!(path = %path.display(), error = %error, "live editor: reload failed; retrying");
                        if let Some(session) = map.get_mut(&path) {
                            session.last_write = Instant::now();
                        }
                    }
                }
            }
        }
    }
}
