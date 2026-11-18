# Working repository

- Use `C:/Users/Ray/Documents/iphoneMirror` on local branch `main` for ongoing work.
- Keep recent fixes and features integrated here. Before starting, inspect `git status`, recent commits, and fetch `origin` when network access is available. Preserve pending changes when synchronizing.
- Do not create another branch or linked worktree unless the user explicitly requests one. If reviewing work elsewhere, integrate the approved changes and fixes into this `main` before delivering a build.
- Build deliveries from this integrated working tree. Use `outputs/iPhoneMirror-main` as the current delivery directory and do not overwrite a running program; stop or update it only with appropriate task authorization.
- Keep settings in the three existing categories: application, projection, and reverse control. Display each setting group directly; do not add collapsible groups or hide settings behind merged groups.
- Recovery archives in `work/main-consolidation` are historical backups, not active working copies. Do not resume development there.

# iUsbBridge synchronization

- Whenever iUsbBridge is modified while working in iPhoneMirror, synchronize the relevant changes back to `C:/Users/Ray/Documents/iUsbBridge` on branch `main` and commit them there as part of the same task. Do not leave bridge changes only in iPhoneMirror.
- Include affected bridge source and helper modules, protocol/contracts, packaging, tests, and documentation. Map iPhoneMirror's `tools/` sources to iUsbBridge's `src/` and keep the standalone project usable.
- Inspect the iUsbBridge repository status and recent commits, fetch `origin` when available, and preserve unrelated pending changes before synchronizing. Run validation appropriate to the changes before committing, and report the resulting commit.
- Keep iUsbBridge `main` on the maintained Python implementation. The Rust implementation belongs on `codex/rust-backend`; do not replace Python `main` with it.
