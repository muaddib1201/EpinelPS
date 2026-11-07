# Character detail toggle persistence

**Goal:** Preserve the character detail background and information switches across client restarts.

**Approach:** Implement the existing toggle/list protocol using per-account dictionaries keyed by character serial number, stored by the existing JsonDb save path. Preserve explicit false values and legacy-save defaults. Include the favorite-item wallpaper preference from the same list contract.

- [x] Reproduce lost preferences with real protobuf dispatch and isolated test saves.
- [x] Implement the four toggle protocol handlers and persisted user fields.
- [x] Verify enable/disable, character/account isolation, old saves, invalid characters, independent lobby wallpaper, and a fresh process startup.
- [x] Run existing regression projects and Release publish.
- [x] Back up the deployed binary and current saves, deploy, restart the server, and verify startup.

Work in the current patched source so earlier local fixes remain included. Tests must never use the live server's database. Back up the original modified UserModel.cs before editing. No client patch or schema migration is required.

**Red evidence:** The real dispatcher returned empty fallback responses for the missing routes. Seven persistence/validation scenarios failed with missing saved state; the unchanged legacy-default scenario passed. Log: `character-toggle-red.log` in the NIKKE workspace.

**Green evidence:** CharacterToggleRegression: 8 scenarios passed, followed by a separate process reading the saved background=true, information=false, and favorite-wallpaper=true states. SupportCharacterRegression: 20 scenarios passed. TowerRegression: 5/5 passed. Logs: `character-toggle-CharacterToggleRegression.log`, `character-toggle-restart.log`, `character-toggle-SupportCharacterRegression.log`, and `character-toggle-TowerRegression.log`.

**Initial publish (superseded):** `.tools/publish-character-toggles-20261103/EpinelPS.exe` in the NIKKE workspace. SHA256: `C4013C0B3C59D88071F777D009636D5B44D24E058F812FE32899AD3B59DDB396`.

**Deployment:** The user explicitly approved deployment and restart after the initial automatic approval rejection. Backup: `D:/Claude Lab/NIKKE/backup/server-20261103-014002-character-toggles` (binary, JSON save, SQLite files and configuration).

**Current-source verification:** Other source changes and the duplicate-core repair were deployed while awaiting approval. The initially staged binary was briefly installed, then the backed-up newer binary was restored immediately to preserve those changes. All four regression projects were rerun from the latest source: character toggles 8/8, duplicate core 24/24, support characters 20/20, tower 5/5 (57 total), plus separate-process toggle persistence. Final Release publish: `.tools/publish-character-toggles-20261103-final/EpinelPS.exe`, SHA256 `E0C764AFB258D86B9EEAC05A653D6C399FD00B777FC0623592C21135CA3EE5AD`, matching the restored running executable exactly. Final deployment details and HTTP/HTTPS readiness results are recorded in `character-toggle-deploy.json`. Actual game-client restart acceptance remains pending.
