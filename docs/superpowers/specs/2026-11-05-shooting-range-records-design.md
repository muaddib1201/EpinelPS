# Shooting range records

The user approved completing the missing score submission, personal battle history and guild leaderboard flow after the live log showed unhandled `/shootingrange/v2/set` and `/shootingrange/getbattlelog` requests.

Use the existing protobuf messages, MVC authentication and atomic `db.json` saves. Keep guild best scores by exact regular stage ID and member ID. Return the existing fifteen element/length boards, now populated with descending best scores for current members. A lower or equal score does not replace a personal best. Equal scores use user ID for stable ordering. Preserve 64-bit scores.

Keep the latest 100 personal battles as protobuf snapshots, including the submitted result, exact stage, UTC time and actual squad. Prefer the submitted battle roster, using saved element-specific presets when battle telemetry is absent. Preserve character appearance and battle statistics at submission time. This history limit is an implementation choice to bound save size; best scores are stored separately and survive history pruning.

Fixed-spec practice is retained in history and does not enter the regular leaderboard (the client has only fifteen regular category boards). This is an implementation choice to keep the two sets of combat conditions separate. Do not fabricate earlier scores: the old unhandled requests did not store them.

Use the authenticated transport nonce plus request hash to replay a retained submission's original response without duplicating history. Reject invalid sessions, nonexistent stages and negative scores. Guildless submissions return the protocol's NotMember result. Failed saves roll back both history and scores. Existing saves without the new fields load as empty, and empty fields are omitted when saving.

Approach: extend the current JSON save instead of adding a separate SQLite transaction, so history, replay receipts and rankings commit together. Preserve all existing source fixes and deployed configuration. Validate in isolated test databases, publish the current complete source, back up the live executable and saves, and install only while the game is closed.
